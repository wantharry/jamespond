using System.Collections.Generic;
using Blocks.Gameplay.Core;
using Unity.AI.Navigation;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Blocks.Gameplay.Stealth.Editor
{
    /// <summary>
    /// One-click scene setup for the stealth module. Bakes a NavMesh, then drops fully wired guards
    /// with patrol routes onto walkable ground.
    /// </summary>
    /// <remarks>
    /// This exists because assembling a guard by hand means adding six components, setting two layer
    /// masks correctly, and finding navigable positions to stand on — easy to get subtly wrong in
    /// ways that fail silently. Everything here is undoable and safe to run more than once.
    /// </remarks>
    public static class StealthSetupTool
    {
        #region Constants

        private const string NavSurfaceName = "NavMesh Surface (Stealth)";
        private const string GuardRootName = "Guards";
        private const int GuardCount = 3;
        private const int WaypointsPerGuard = 3;
        private const float PatrolRadius = 9f;

        #endregion

        #region Menu Items

        /// <summary>
        /// Bakes navigation and populates the open scene with patrolling guards.
        /// </summary>
        [MenuItem("Tools/Stealth/Set Up Guards In Current Scene", false, 0)]
        public static void SetUpGuards()
        {
            NavMeshSurface surface = EnsureNavMeshSurface();

            EditorUtility.DisplayProgressBar("Stealth Setup", "Baking NavMesh...", 0.3f);
            surface.BuildNavMesh();
            EditorUtility.ClearProgressBar();

            // Sample the baked navmesh for somewhere to actually put the guards. Without this we
            // would be guessing at coordinates that may be inside a wall or off the level.
            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
            if (triangulation.vertices == null || triangulation.vertices.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "Stealth Setup",
                    "The NavMesh baked empty, so there is nowhere to place guards.\n\n" +
                    "The usual cause is that the level geometry is not marked Navigation Static, " +
                    "or the NavMesh Surface's Collect Objects setting is not picking it up.\n\n" +
                    "Select '" + NavSurfaceName + "' in the Hierarchy and check its settings.",
                    "OK");
                return;
            }

            GameObject root = GameObject.Find(GuardRootName);
            if (root == null)
            {
                root = new GameObject(GuardRootName);
                Undo.RegisterCreatedObjectUndo(root, "Create Guards Root");
            }

            int placed = 0;
            for (int i = 0; i < GuardCount; i++)
            {
                if (TryPickNavMeshPoint(triangulation, out Vector3 position))
                {
                    CreateGuard(root.transform, position, i + 1);
                    placed++;
                }
            }

            EditorSceneManager.MarkAllScenesDirty();
            Selection.activeGameObject = root;
            SceneView.FrameLastActiveSceneView();

            EditorUtility.DisplayDialog(
                "Stealth Setup",
                $"Baked the NavMesh and placed {placed} guard(s) under '{GuardRootName}'.\n\n" +
                "Press Play, then Start Host. Guards only run on the server, so nothing moves " +
                "until hosting begins.\n\n" +
                "Select a guard to see its vision cone and patrol route in the Scene view.",
                "OK");
        }

        /// <summary>
        /// Re-bakes navigation without touching guards. Use after editing level geometry.
        /// </summary>
        [MenuItem("Tools/Stealth/Rebake NavMesh Only", false, 1)]
        public static void RebakeNavMesh()
        {
            NavMeshSurface surface = EnsureNavMeshSurface();
            surface.BuildNavMesh();
            EditorSceneManager.MarkAllScenesDirty();
            Debug.Log("[StealthSetup] NavMesh rebaked.", surface);
        }

        /// <summary>
        /// Adds any stealth components that guards already in the scene are missing, and refreshes
        /// their layer masks.
        /// </summary>
        /// <remarks>
        /// Adding a component to the codebase does not retroactively attach it to GameObjects that
        /// already exist in a scene. Guards built before a component was written keep working but
        /// silently lack the new behaviour, so this repairs them in place rather than forcing a
        /// delete-and-recreate that would lose hand-placed positions and routes.
        /// </remarks>
        [MenuItem("Tools/Stealth/Upgrade Existing Guards", false, 2)]
        public static void UpgradeExistingGuards()
        {
            GuardBrain[] brains = Object.FindObjectsByType<GuardBrain>(FindObjectsInactive.Include);
            if (brains.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "Stealth Setup",
                    "No guards found in the open scene.\n\nUse 'Set Up Guards In Current Scene' first.",
                    "OK");
                return;
            }

            int upgraded = 0;

            foreach (GuardBrain brain in brains)
            {
                GameObject go = brain.gameObject;
                bool changed = false;

                GuardVision vision = go.GetComponent<GuardVision>();
                if (vision == null)
                {
                    vision = Undo.AddComponent<GuardVision>(go);
                    changed = true;
                }
                ConfigureVisionMasks(vision);

                if (go.GetComponent<GuardPatrol>() == null)
                {
                    Undo.AddComponent<GuardPatrol>(go);
                    changed = true;
                }

                GuardWeapon weapon = go.GetComponent<GuardWeapon>();
                if (weapon == null)
                {
                    weapon = Undo.AddComponent<GuardWeapon>(go);
                    changed = true;
                }
                ConfigureWeaponMask(weapon);

                if (changed)
                {
                    upgraded++;
                }
            }

            EditorSceneManager.MarkAllScenesDirty();

            EditorUtility.DisplayDialog(
                "Stealth Setup",
                $"Checked {brains.Length} guard(s); added missing components to {upgraded}.\n\n" +
                "Layer masks were refreshed on all of them.\n\n" +
                "Save the scene (Ctrl+S) to keep this.",
                "OK");
        }

        /// <summary>
        /// Adds <see cref="PlayerCrouch"/> to every player prefab in the project that has a
        /// <see cref="CoreMovement"/>.
        /// </summary>
        /// <remarks>
        /// Applied to the prefab asset rather than a scene instance so it survives respawns: the
        /// player is spawned from the prefab by Netcode, so a component added only to an instance
        /// would vanish the first time the player respawns.
        /// </remarks>
        [MenuItem("Tools/Stealth/Add Crouch To Player Prefabs", false, 3)]
        public static void AddCrouchToPlayerPrefabs()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab");
            int modified = 0;
            System.Text.StringBuilder names = new System.Text.StringBuilder();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    continue;
                }

                // The player is identified by carrying the movement motor, not by name, so renamed
                // or duplicated player prefabs are still found.
                if (prefab.GetComponent<CoreMovement>() == null)
                {
                    continue;
                }

                if (prefab.GetComponent<PlayerCrouch>() != null)
                {
                    continue;
                }

                GameObject root = PrefabUtility.LoadPrefabContents(path);
                if (root.GetComponent<PlayerCrouch>() == null)
                {
                    root.AddComponent<PlayerCrouch>();
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    modified++;
                    names.AppendLine("  " + System.IO.Path.GetFileNameWithoutExtension(path));
                }
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog(
                "Stealth Setup",
                modified > 0
                    ? $"Added PlayerCrouch to {modified} player prefab(s):\n\n{names}\n" +
                      "Hold Left Ctrl in play mode to crouch."
                    : "No player prefabs needed changing.\n\nEither crouch is already added, or no " +
                      "prefab in the project has a CoreMovement component.",
                "OK");
        }

        /// <summary>
        /// Removes everything this tool created, for a clean retry.
        /// </summary>
        [MenuItem("Tools/Stealth/Remove Generated Guards", false, 20)]
        public static void RemoveGuards()
        {
            GameObject root = GameObject.Find(GuardRootName);
            if (root == null)
            {
                Debug.Log("[StealthSetup] Nothing to remove.");
                return;
            }

            Undo.DestroyObjectImmediate(root);
            EditorSceneManager.MarkAllScenesDirty();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Finds the tool's NavMesh surface in the scene, creating one configured to sweep the whole
        /// scene if it does not exist yet.
        /// </summary>
        private static NavMeshSurface EnsureNavMeshSurface()
        {
            GameObject existing = GameObject.Find(NavSurfaceName);
            if (existing != null)
            {
                NavMeshSurface found = existing.GetComponent<NavMeshSurface>();
                if (found != null)
                {
                    return found;
                }

                return Undo.AddComponent<NavMeshSurface>(existing);
            }

            GameObject host = new GameObject(NavSurfaceName);
            Undo.RegisterCreatedObjectUndo(host, "Create NavMesh Surface");

            NavMeshSurface surface = Undo.AddComponent<NavMeshSurface>(host);

            // Sweep every renderer in the scene rather than relying on Navigation Static flags,
            // which the sample levels do not set.
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.RenderMeshes;

            return surface;
        }

        /// <summary>
        /// Picks a random point on the baked navmesh, biased away from the very edges so guards do
        /// not spawn hard against a wall.
        /// </summary>
        private static bool TryPickNavMeshPoint(NavMeshTriangulation triangulation, out Vector3 position)
        {
            position = Vector3.zero;

            if (triangulation.indices == null || triangulation.indices.Length < 3)
            {
                return false;
            }

            // Average a triangle's corners to land inside it rather than on a boundary vertex.
            int triangleCount = triangulation.indices.Length / 3;
            int triangle = Random.Range(0, triangleCount) * 3;

            Vector3 centre = (triangulation.vertices[triangulation.indices[triangle]] +
                              triangulation.vertices[triangulation.indices[triangle + 1]] +
                              triangulation.vertices[triangulation.indices[triangle + 2]]) / 3f;

            if (NavMesh.SamplePosition(centre, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            {
                position = hit.position;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Builds a single fully wired guard plus its patrol route.
        /// </summary>
        private static void CreateGuard(Transform parent, Vector3 position, int index)
        {
            GameObject guard = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            guard.name = $"Guard {index}";
            guard.transform.SetParent(parent, false);
            guard.transform.position = position + Vector3.up * 1f;
            Undo.RegisterCreatedObjectUndo(guard, "Create Guard");

            // The capsule's own collider would block the guard's sight rays and catch its own
            // detection sphere, so it goes.
            Object.DestroyImmediate(guard.GetComponent<Collider>());

            NavMeshAgent agent = guard.AddComponent<NavMeshAgent>();
            agent.radius = 0.4f;
            agent.height = 2f;
            agent.speed = 1.8f;
            agent.angularSpeed = 320f;
            agent.acceleration = 12f;

            // Netcode: scene-placed NetworkObjects are spawned automatically when hosting starts.
            // NetworkTransform is what makes the movement visible to connected clients.
            guard.AddComponent<NetworkObject>();
            guard.AddComponent<NetworkTransform>();

            GuardVision vision = guard.AddComponent<GuardVision>();
            GuardPatrol patrol = guard.AddComponent<GuardPatrol>();
            GuardWeapon weapon = guard.AddComponent<GuardWeapon>();
            guard.AddComponent<GuardBrain>();

            ConfigureVisionMasks(vision);
            ConfigureWeaponMask(weapon);
            CreatePatrolRoute(guard.transform, patrol, position, index);
            TintGuard(guard);
        }

        /// <summary>
        /// Sets the two layer masks that decide whether detection works at all.
        /// </summary>
        /// <remarks>
        /// The obstacle mask must exclude the Player layer. If the player's own collider counts as
        /// an obstruction it blocks every sight ray aimed at it, and guards silently never see
        /// anything — the single easiest way to get a "broken" stealth system that compiles fine.
        /// </remarks>
        private static void ConfigureVisionMasks(GuardVision vision)
        {
            int playerLayer = LayerMask.NameToLayer("Player");
            int hitBoxLayer = LayerMask.NameToLayer("PlayerHitBox");

            int targetMask = 0;
            if (playerLayer >= 0)
            {
                targetMask |= 1 << playerLayer;
            }
            if (hitBoxLayer >= 0)
            {
                targetMask |= 1 << hitBoxLayer;
            }

            // Fall back to everything-but-nothing rather than leaving a mask of 0, which would
            // detect nothing at all.
            if (targetMask == 0)
            {
                targetMask = ~0;
                Debug.LogWarning("[StealthSetup] No 'Player' layer found; target mask left as Everything.");
            }

            // Level geometry blocks sight; the player must not.
            int obstacleMask = 1 << LayerMask.NameToLayer("Default");
            if (playerLayer >= 0)
            {
                obstacleMask &= ~(1 << playerLayer);
            }
            if (hitBoxLayer >= 0)
            {
                obstacleMask &= ~(1 << hitBoxLayer);
            }

            SerializedObject so = new SerializedObject(vision);
            so.FindProperty("targetMask").intValue = targetMask;
            so.FindProperty("obstacleMask").intValue = obstacleMask;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Sets what guard bullets collide with: the player, plus level geometry so shots are
        /// stopped by walls instead of passing through cover.
        /// </summary>
        private static void ConfigureWeaponMask(GuardWeapon weapon)
        {
            int mask = 1 << LayerMask.NameToLayer("Default");

            int playerLayer = LayerMask.NameToLayer("Player");
            if (playerLayer >= 0)
            {
                mask |= 1 << playerLayer;
            }

            int hitBoxLayer = LayerMask.NameToLayer("PlayerHitBox");
            if (hitBoxLayer >= 0)
            {
                mask |= 1 << hitBoxLayer;
            }

            SerializedObject so = new SerializedObject(weapon);
            so.FindProperty("hitMask").intValue = mask;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Scatters waypoints on navigable ground around the guard and assigns them to its patrol.
        /// </summary>
        private static void CreatePatrolRoute(Transform guard, GuardPatrol patrol, Vector3 origin, int index)
        {
            GameObject routeRoot = new GameObject($"Guard {index} Route");
            routeRoot.transform.SetParent(guard.parent, false);
            routeRoot.transform.position = origin;
            Undo.RegisterCreatedObjectUndo(routeRoot, "Create Patrol Route");

            List<Transform> points = new List<Transform>();

            for (int i = 0; i < WaypointsPerGuard; i++)
            {
                // Spread waypoints evenly around the guard so the route is a loop, not a line.
                float angle = (360f / WaypointsPerGuard) * i * Mathf.Deg2Rad;
                Vector3 candidate = origin + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * PatrolRadius;

                // Only keep points the guard can actually walk to.
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, PatrolRadius, NavMesh.AllAreas))
                {
                    continue;
                }

                GameObject waypoint = new GameObject($"Waypoint {i + 1}");
                waypoint.transform.SetParent(routeRoot.transform, false);
                waypoint.transform.position = hit.position;
                points.Add(waypoint.transform);
            }

            SerializedObject so = new SerializedObject(patrol);
            SerializedProperty list = so.FindProperty("waypoints");
            list.arraySize = points.Count;
            for (int i = 0; i < points.Count; i++)
            {
                list.GetArrayElementAtIndex(i).objectReferenceValue = points[i];
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Gives guards a distinct colour so they are obvious against the sample level's grey.
        /// </summary>
        private static void TintGuard(GameObject guard)
        {
            Renderer renderer = guard.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null)
            {
                return;
            }

            Material material = new Material(shader) { color = new Color(0.75f, 0.15f, 0.15f) };
            renderer.sharedMaterial = material;
        }

        #endregion
    }
}
