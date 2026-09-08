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

        /// <summary>Layer the guard body sits on: in the player weapons’ hit mask, out of the guards’ own.</summary>
        private const string GuardLayerName = "Guard";
        private const int GuardCount = 3;
        private const int WaypointsPerGuard = 3;
        private const float PatrolRadius = 9f;

        /// <summary>The visual half of the player prefab, without any of its player-only components.</summary>
        private const string PlayerVisualPath = "Assets/Core/Art/Models/Armature_Core.prefab";

        /// <summary>Geometry only. The weapon *prefabs* are networked attachables and cannot be nested under a guard.</summary>
        /// <summary>The player’s controller. Its UpperBody layer is what makes the rifle be held and aimed.</summary>
        private const string ShooterControllerPath = "Assets/Shooter/Art/Animator/ShooterAnimator.controller";

        private const string WeaponModelPath = "Assets/Shooter/Art/Weapons/AssaultRifle/Geo_assaultRifle.fbx";

        /// <summary>Sockets on the shared rig, best first. Right_Hand_Attach is the rig’s own weapon mount.</summary>
        private static readonly string[] WeaponSocketNames = { "Right_Hand_Attach", "Right_Hand" };

        /// <summary>Team-colour suffixes the sample ships, stripped before asking for the red variant.</summary>
        private static readonly string[] TeamColourSuffixes = { "_white", "_blue", "_orange", "_red" };

        #endregion

        #region Fields

        /// <summary>Suppresses modal dialogs while the tool is driven from outside the editor.</summary>
        private static bool s_Silent;

        #endregion

        #region Menu Items

        /// <summary>
        /// Bakes navigation and populates the open scene with patrolling guards.
        /// </summary>
        [MenuItem("Tools/Stealth/Set Up Guards In Current Scene", false, 0)]
        public static void SetUpGuards()
        {
            // Nothing in this tool works in play mode, and the failures are quiet: 
            // PrefabUtility.InstantiatePrefab returns null, so guards come out bodiless with a
            // NullReferenceException each; MarkAllScenesDirty throws; and anything that does get
            // built is discarded the moment play stops. Refusing outright beats half-building.
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Report("Stop play mode first. Guards built during play are discarded when play ends, " +
                       "and the editor APIs this tool needs do not work while playing.");
                return;
            }

            NavMeshSurface surface = EnsureNavMeshSurface();

            EditorUtility.DisplayProgressBar("Stealth Setup", "Baking NavMesh...", 0.3f);
            surface.BuildNavMesh();
            EditorUtility.ClearProgressBar();

            // Sample the baked navmesh for somewhere to actually put the guards. Without this we
            // would be guessing at coordinates that may be inside a wall or off the level.
            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
            if (triangulation.vertices == null || triangulation.vertices.Length == 0)
            {
                Report(
                    "The NavMesh baked empty, so there is nowhere to place guards.\n\n" +
                    "The usual cause is that the level geometry is not marked Navigation Static, " +
                    "or the NavMesh Surface's Collect Objects setting is not picking it up.\n\n" +
                    "Select '" + NavSurfaceName + "' in the Hierarchy and check its settings.");
                return;
            }

            GameObject root = GameObject.Find(GuardRootName);
            if (root == null)
            {
                root = new GameObject(GuardRootName);
                Undo.RegisterCreatedObjectUndo(root, "Create Guards Root");
            }
            else
            {
                // Clear the previous batch. Without this a second run appends another set, leaving
                // guards from an older build of this tool standing next to the new ones.
                for (int i = root.transform.childCount - 1; i >= 0; i--)
                {
                    Undo.DestroyObjectImmediate(root.transform.GetChild(i).gameObject);
                }
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

            string report = DescribeGuards(root);
            Debug.Log($"[StealthSetup] {report}", root);

            Report(
                $"Baked the NavMesh and placed {placed} guard(s) under '{GuardRootName}'.\n\n" +
                report + "\n\n" +
                "SAVE THE SCENE NOW (Ctrl+S, not in Play mode). Guards live only in memory until you do.\n\n" +
                "Then press Play and Start Host. Guards only run on the server, so nothing moves until hosting begins.");
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
        /// <summary>
        /// Runs the guard setup without any modal dialog, for automation.
        /// </summary>
        /// <remarks>
        /// EditorUtility.DisplayDialog blocks until a human clicks OK. Anything driving the editor
        /// from outside — the Claude bridge, a CI step — hangs on it forever, because the click it
        /// is waiting for can never arrive. This variant reports to the console instead.
        /// </remarks>
        [MenuItem("Tools/Stealth/Set Up Guards (No Dialog)", false, 4)]
        public static void SetUpGuardsSilently()
        {
            s_Silent = true;
            try
            {
                SetUpGuards();
            }
            finally
            {
                s_Silent = false;
            }
        }

        /// <summary>
        /// Shows a message as a dialog, or logs it when running unattended.
        /// </summary>
        private static void Report(string message)
        {
            if (s_Silent)
            {
                Debug.Log("[StealthSetup] " + message.Replace("\n\n", " "));
                return;
            }

            EditorUtility.DisplayDialog("Stealth Setup", message, "OK");
        }

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
            GameObject guard = new GameObject($"Guard {index}");
            guard.transform.SetParent(parent, false);

            // The pivot sits directly on the navmesh: the player model’s origin is at its feet,
            // and GuardVision measures eye height upwards from the pivot. The old capsule needed a
            // metre of offset only because a primitive’s pivot is its centre.
            guard.transform.position = position;
            Undo.RegisterCreatedObjectUndo(guard, "Create Guard");

            Transform muzzle = AttachPlayerVisual(guard);

            NavMeshAgent agent = guard.AddComponent<NavMeshAgent>();
            agent.radius = 0.4f;
            agent.height = 2f;
            agent.speed = 1.8f;
            agent.angularSpeed = 320f;
            agent.acceleration = 12f;

            AddBody(guard);

            // Netcode: scene-placed NetworkObjects are spawned automatically when hosting starts.
            // NetworkTransform is what makes the movement visible to connected clients.
            guard.AddComponent<NetworkObject>();
            guard.AddComponent<NetworkTransform>();

            GuardVision vision = guard.AddComponent<GuardVision>();
            GuardPatrol patrol = guard.AddComponent<GuardPatrol>();
            GuardWeapon weapon = guard.AddComponent<GuardWeapon>();
            guard.AddComponent<GuardBrain>();
            guard.AddComponent<GuardAnimatorDriver>();
            guard.AddComponent<GuardHealth>();
            guard.AddComponent<GuardVisionCone>();
            guard.AddComponent<GuardDeathAnimation>();

            ConfigureVisionMasks(vision);
            ConfigureWeaponMask(weapon);
            ConfigureMuzzle(weapon, muzzle);
            CreatePatrolRoute(guard.transform, patrol, position, index);
        }

        /// <summary>
        /// Summarises what the guards actually ended up with.
        /// </summary>
        /// <remarks>
        /// Unity keeps running the last assemblies that compiled, so clicking this menu item while a
        /// script error stands silently runs an older build of the tool and produces guards missing
        /// whatever was added since. Reporting the parts makes that visible instead of leaving it to
        /// be discovered in play.
        /// </remarks>
        private static string DescribeGuards(GameObject root)
        {
            // Only children with a brain are guards. CreatePatrolRoute parents each route as a
            // sibling of its guard, so counting every child reports double.
            int total = 0;
            int armed = 0;
            int killable = 0;
            int muzzled = 0;

            for (int i = 0; i < root.transform.childCount; i++)
            {
                GameObject guard = root.transform.GetChild(i).gameObject;
                if (guard.GetComponent<GuardBrain>() == null)
                {
                    continue;
                }

                total++;

                if (guard.GetComponentInChildren<SkinnedMeshRenderer>(true) != null && guard.transform.Find("Visual") != null)
                {
                    // Body present; the weapon lives under the rig socket.
                    armed += FindDescendant(guard.transform, "Weapon") != null ? 1 : 0;
                    muzzled += FindDescendant(guard.transform, "Muzzle") != null ? 1 : 0;
                }

                if (guard.GetComponent<GuardHealth>() != null && guard.GetComponent<Collider>() != null)
                {
                    killable++;
                }
            }

            return $"{total} guard(s): {armed} armed, {muzzled} firing from the barrel, {killable} killable.";
        }

        /// <summary>
        /// Depth-first search for a descendant by exact name.
        /// </summary>
        private static Transform FindDescendant(Transform root, string name)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == name)
                {
                    return child;
                }
            }

            return null;
        }

        /// <summary>
        /// Points the weapon at the barrel tip built by <see cref="CreateMuzzle"/>.
        /// </summary>
        private static void ConfigureMuzzle(GuardWeapon weapon, Transform muzzle)
        {
            if (muzzle == null)
            {
                return;
            }

            SerializedObject so = new SerializedObject(weapon);
            so.FindProperty("muzzle").objectReferenceValue = muzzle;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Gives the guard a body the player can shoot.
        /// </summary>
        /// <remarks>
        /// The collider goes on its own <c>Guard</c> layer, which is in the player weapons’ hit mask
        /// but in neither the guards’ obstacle mask nor their own weapons’ hit mask. That is what
        /// keeps guards shootable without them blocking each other’s line of sight or killing each
        /// other in a crossfire.
        ///
        /// A guard’s own sight ray starts inside this collider, and Physics.Raycast does not report
        /// a collider it originates within, so the body cannot blind its owner either.
        /// </remarks>
        private static void AddBody(GameObject guard)
        {
            int guardLayer = LayerMask.NameToLayer(GuardLayerName);
            if (guardLayer >= 0)
            {
                guard.layer = guardLayer;
            }
            else
            {
                Debug.LogWarning($"[StealthSetup] No ‘{GuardLayerName}’ layer; guard left on Default, where its own bullets can hit it.", guard);
            }

            CapsuleCollider body = guard.AddComponent<CapsuleCollider>();
            body.height = 1.8f;
            body.radius = 0.3f;
            body.center = new Vector3(0f, 0.9f, 0f);
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
        /// Gives the guard the player’s own body, in the red team skin.
        /// </summary>
        /// <remarks>
        /// Only the model is instantiated, never the player prefab. The player prefab carries a
        /// NetworkObject, input, camera rigs and ability components; nesting it inside another
        /// NetworkObject is invalid in Netcode and would give every guard a camera.
        /// </remarks>
        private static Transform AttachPlayerVisual(GameObject guard)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerVisualPath);
            if (source == null)
            {
                Debug.LogWarning($"[StealthSetup] No player model at {PlayerVisualPath}; guard {guard.name} has no body.", guard);
                return null;
            }

            GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(source, guard.transform);
            visual.name = "Visual";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            Undo.RegisterCreatedObjectUndo(visual, "Create Guard Visual");

            // CoreAnimator requires a CoreMovement and is a NetworkAnimator. A guard has neither, so
            // it would log an error per guard on spawn. GuardAnimatorDriver drives the same
            // controller from the guard’s own motion instead.
            foreach (CoreAnimator playerAnimator in visual.GetComponentsInChildren<CoreAnimator>(true))
            {
                Object.DestroyImmediate(playerAnimator);
            }

            // Any collider here would block the guard’s own sight rays and trip its detection
            // sphere — the same reason the old capsule’s collider was removed.
            foreach (Collider collider in visual.GetComponentsInChildren<Collider>(true))
            {
                Object.DestroyImmediate(collider);
            }

            // The locomotion clips fire footstep and landing events. With CoreAnimator gone nothing
            // answers them, and Unity logs a warning per event per guard.
            foreach (Animator modelAnimator in visual.GetComponentsInChildren<Animator>(true))
            {
                if (modelAnimator.GetComponent<GuardAnimationEvents>() == null)
                {
                    modelAnimator.gameObject.AddComponent<GuardAnimationEvents>();
                }
            }

            UseShooterController(visual);
            PaintRed(visual);
            return AttachWeapon(visual);
        }

        /// <summary>
        /// Swaps the model onto the player’s shooter controller so the rifle is carried and aimed.
        /// </summary>
        /// <remarks>
        /// The model ships with the Core locomotion controller, which has no weapon poses — the arms
        /// hang at the sides and the gun floats in one hand. The shooter controller adds the
        /// UpperBody layer that grips and levels the weapon. GuardAnimatorDriver feeds it.
        /// </remarks>
        private static void UseShooterController(GameObject visual)
        {
            RuntimeAnimatorController controller =
                AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ShooterControllerPath);

            if (controller == null)
            {
                Debug.LogWarning($"[StealthSetup] No shooter controller at {ShooterControllerPath}; guards keep the Core poses and will not hold the gun properly.", visual);
                return;
            }

            foreach (Animator modelAnimator in visual.GetComponentsInChildren<Animator>(true))
            {
                modelAnimator.runtimeAnimatorController = controller;
            }
        }

        /// <summary>
        /// Puts a rifle in the guard’s hand, on the same rig socket the player’s weapon uses.
        /// </summary>
        /// <remarks>
        /// This is the weapon *model*, not <c>Pfb_assaultRifle</c>. The weapon prefabs carry a
        /// NetworkObject and an AttachableBehaviour and are spawned through WeaponController’s
        /// networked attachment system; nesting one under the guard’s NetworkObject at scene-build
        /// time is invalid. Guard damage is hitscan out of <see cref="GuardWeapon"/> and does not
        /// read the model, so the gun here is purely what the player sees.
        /// </remarks>
        private static Transform AttachWeapon(GameObject visual)
        {
            Transform socket = FindWeaponSocket(visual);
            if (socket == null)
            {
                Debug.LogWarning($"[StealthSetup] No weapon socket on the rig (looked for {string.Join(", ", WeaponSocketNames)}); guard is unarmed.", visual);
                return null;
            }

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(WeaponModelPath);
            if (model == null)
            {
                Debug.LogWarning($"[StealthSetup] No weapon model at {WeaponModelPath}; guard is unarmed.", visual);
                return null;
            }

            GameObject weapon = (GameObject)PrefabUtility.InstantiatePrefab(model, socket);
            weapon.name = "Weapon";
            weapon.transform.localPosition = Vector3.zero;
            weapon.transform.localRotation = Quaternion.identity;
            Undo.RegisterCreatedObjectUndo(weapon, "Create Guard Weapon");

            // A collider here would sit inside the guard’s own detection sphere and block its
            // sight rays, the same reason the body colliders go.
            foreach (Collider collider in weapon.GetComponentsInChildren<Collider>(true))
            {
                Object.DestroyImmediate(collider);
            }

            return CreateMuzzle(weapon, visual.transform.parent != null ? visual.transform.parent : visual.transform);
        }

        /// <summary>
        /// Marks the barrel tip so shots leave the gun rather than the guard’s chest.
        /// </summary>
        /// <remarks>
        /// The rifle model has no muzzle node — only grip, body and trigger meshes — so the tip is
        /// measured instead: take the weapon’s combined render bounds and walk forward along the
        /// guard’s facing to the front face. Measuring against the guard rather than the model avoids
        /// guessing which local axis the artist pointed the barrel down.
        ///
        /// Parented to the weapon, so it tracks the hand through every animation.
        /// </remarks>
        private static Transform CreateMuzzle(GameObject weapon, Transform guard)
        {
            Renderer[] renderers = weapon.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return null;
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            Vector3 forward = guard.forward;
            float reach = Vector3.Dot(bounds.extents, new Vector3(Mathf.Abs(forward.x), Mathf.Abs(forward.y), Mathf.Abs(forward.z)));

            GameObject muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(weapon.transform, false);
            muzzle.transform.position = bounds.center + forward * reach;
            muzzle.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            Undo.RegisterCreatedObjectUndo(muzzle, "Create Guard Muzzle");

            return muzzle.transform;
        }

        /// <summary>
        /// Finds the rig socket to hang the weapon from, preferring the dedicated attach point.
        /// </summary>
        private static Transform FindWeaponSocket(GameObject visual)
        {
            Transform[] bones = visual.GetComponentsInChildren<Transform>(true);
            foreach (string socketName in WeaponSocketNames)
            {
                foreach (Transform bone in bones)
                {
                    if (bone.name == socketName)
                    {
                        return bone;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Swaps every material on the guard model for the red team variant the sample ships.
        /// </summary>
        /// <remarks>
        /// Preferring the shipped <c>*_red</c> material over a runtime tint keeps the guard visually
        /// identical to a red-team player — same shader, same maps, same smoothness. The tint is
        /// only a fallback for materials with no red sibling.
        /// </remarks>
        private static void PaintRed(GameObject visual)
        {
            foreach (Renderer renderer in visual.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    materials[i] = ToRed(materials[i]);
                }

                renderer.sharedMaterials = materials;
            }
        }

        /// <summary>
        /// Resolves a material to its red team sibling, falling back to a red-tinted copy.
        /// </summary>
        private static Material ToRed(Material source)
        {
            if (source == null)
            {
                return null;
            }

            string baseName = source.name;
            foreach (string suffix in TeamColourSuffixes)
            {
                if (baseName.EndsWith(suffix))
                {
                    baseName = baseName.Substring(0, baseName.Length - suffix.Length);
                    break;
                }
            }

            string wanted = baseName + "_red";
            foreach (string guid in AssetDatabase.FindAssets($"{wanted} t:Material"))
            {
                Material candidate = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (candidate != null && candidate.name == wanted)
                {
                    return candidate;
                }
            }

            // No shipped red variant: copy the original so the tint cannot leak onto the player,
            // who shares these material assets.
            return new Material(source) { color = new Color(0.75f, 0.15f, 0.15f) };
        }

        #endregion
    }
}
