using System.Collections.Generic;
using Unity.Cinemachine;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Blocks.Gameplay.Chase.Editor
{
    /// <summary>
    /// One-click scene setup for the chase: a garage with a shutter, a drivable car, police parked
    /// out of sight, an extraction point, and the director that sequences them.
    /// </summary>
    /// <remarks>
    /// Everything is built from primitives. The project ships no vehicle art of any kind, so this
    /// makes placeholder shapes that are the right size and pivot for the handling to be tuned
    /// against. Swapping in a real model later means parenting it under the body and deleting the
    /// boxes; nothing in the driving code reads the visuals.
    ///
    /// Placement is sampled off the baked NavMesh so the garage and the extraction point land on
    /// walkable ground, and so the police have somewhere to drive. That reuses the guards' NavMesh
    /// rather than baking a second one.
    /// </remarks>
    public static class ChaseSetupTool
    {
        #region Constants

        private const string RootName = "Chase";
        private const int PoliceCount = 3;
        private const float CarLength = 4.2f;
        private const float CarWidth = 1.9f;
        private const float CarHeight = 1.3f;

        #endregion

        #region Menu Items

        [MenuItem("Tools/Chase/Set Up Chase In Current Scene", false, 0)]
        public static void SetUpChase()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[ChaseSetup] Stop play mode first. Objects built during play are discarded when play ends.");
                return;
            }

            NavMeshTriangulation navMesh = NavMesh.CalculateTriangulation();
            if (navMesh.vertices == null || navMesh.vertices.Length == 0)
            {
                Debug.LogError("[ChaseSetup] No NavMesh in this scene. Run Tools > Stealth > Set Up Guards first; " +
                               "it bakes the NavMesh this needs for placement and for the police to drive on.");
                return;
            }

            GameObject existing = GameObject.Find(RootName);
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing);
            }

            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Create Chase");

            FindPlacement(navMesh, out Vector3 garageSpot, out Vector3 extractionSpot);

            GarageDoor door = BuildGarage(root.transform, garageSpot, out Vector3 carSpot, out Quaternion facing);
            CarController car = BuildPlayerCar(root.transform, carSpot, facing, out Transform seat);
            CinemachineCamera camera = BuildChaseCamera(root.transform, car.transform);
            ExtractionZone extraction = BuildExtraction(root.transform, extractionSpot);
            PursuitCar[] police = BuildPolice(root.transform, garageSpot);

            BuildDirector(root.transform, door, car, seat, extraction, camera, police);

            EditorSceneManager.MarkAllScenesDirty();
            Selection.activeGameObject = root;

            Debug.Log($"[ChaseSetup] Built the chase: garage at {garageSpot}, extraction {Vector3.Distance(garageSpot, extractionSpot):F0}m away, " +
                      $"{police.Length} police car(s). Kill every guard to trigger it. SAVE THE SCENE.", root);
        }

        [MenuItem("Tools/Chase/Remove Chase", false, 20)]
        public static void RemoveChase()
        {
            GameObject existing = GameObject.Find(RootName);
            if (existing == null)
            {
                Debug.Log("[ChaseSetup] Nothing to remove.");
                return;
            }

            Undo.DestroyObjectImmediate(existing);
            EditorSceneManager.MarkAllScenesDirty();
        }

        #endregion

        #region Placement

        /// <summary>
        /// Picks the garage spot and puts the extraction point as far from it as the level allows.
        /// </summary>
        /// <remarks>
        /// Sampling the triangulation rather than guessing coordinates: a hard-coded position lands
        /// inside a wall the moment the level changes. Farthest-apart gives the chase whatever
        /// running room the level actually has.
        /// </remarks>
        private static void FindPlacement(NavMeshTriangulation navMesh, out Vector3 garage, out Vector3 extraction)
        {
            garage = navMesh.vertices[0];
            extraction = navMesh.vertices[0];
            float best = 0f;

            // Strided rather than exhaustive: the triangulation can hold tens of thousands of
            // vertices and an all-pairs search would hang the editor for a result no better.
            int stride = Mathf.Max(1, navMesh.vertices.Length / 256);

            for (int a = 0; a < navMesh.vertices.Length; a += stride)
            {
                for (int b = a + stride; b < navMesh.vertices.Length; b += stride)
                {
                    float distance = Vector3.SqrMagnitude(navMesh.vertices[a] - navMesh.vertices[b]);
                    if (distance > best)
                    {
                        best = distance;
                        garage = navMesh.vertices[a];
                        extraction = navMesh.vertices[b];
                    }
                }
            }
        }

        #endregion

        #region Builders

        private static GarageDoor BuildGarage(Transform parent, Vector3 spot, out Vector3 carSpot, out Quaternion facing)
        {
            var garage = new GameObject("Garage");
            garage.transform.SetParent(parent, false);
            garage.transform.position = spot;

            // Opening faces the extraction side of the level; the car drives straight out.
            facing = Quaternion.identity;
            carSpot = spot + Vector3.up * (CarHeight * 0.5f);

            const float width = 6f;
            const float depth = 8f;
            const float height = 4.5f;
            const float thickness = 0.3f;

            Block(garage.transform, "Wall Left", new Vector3(-width * 0.5f, height * 0.5f, 0f), new Vector3(thickness, height, depth));
            Block(garage.transform, "Wall Right", new Vector3(width * 0.5f, height * 0.5f, 0f), new Vector3(thickness, height, depth));
            Block(garage.transform, "Wall Back", new Vector3(0f, height * 0.5f, -depth * 0.5f), new Vector3(width, height, thickness));
            Block(garage.transform, "Roof", new Vector3(0f, height, 0f), new Vector3(width, thickness, depth));

            GameObject shutter = Block(garage.transform, "Door", new Vector3(0f, height * 0.5f, depth * 0.5f), new Vector3(width, height, thickness));
            Paint(shutter, new Color(0.85f, 0.5f, 0.1f));

            // The door is a NetworkObject so its open fraction replicates; without one the
            // NetworkVariable inside it never initialises.
            shutter.AddComponent<NetworkObject>();
            GarageDoor door = shutter.AddComponent<GarageDoor>();

            return door;
        }

        private static CarController BuildPlayerCar(Transform parent, Vector3 spot, Quaternion facing, out Transform seat)
        {
            GameObject car = BuildCarBody("Player Car", parent, spot, facing, new Color(0.15f, 0.2f, 0.6f));

            var body = car.AddComponent<Rigidbody>();
            body.mass = 1200f;
            body.linearDamping = 0.2f;
            body.angularDamping = 4f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            car.AddComponent<NetworkObject>();
            car.AddComponent<NetworkTransform>();
            CarController controller = car.AddComponent<CarController>();

            var seatObject = new GameObject("Driver Seat");
            seatObject.transform.SetParent(car.transform, false);
            seatObject.transform.localPosition = new Vector3(-0.4f, CarHeight * 0.5f, 0.2f);
            seat = seatObject.transform;

            return controller;
        }

        private static PursuitCar[] BuildPolice(Transform parent, Vector3 garageSpot)
        {
            var police = new List<PursuitCar>();

            var pen = new GameObject("Police");
            pen.transform.SetParent(parent, false);

            for (int i = 0; i < PoliceCount; i++)
            {
                // Parked under the garage until the director needs them. Out of sight rather than
                // inactive at build time, so their NetworkObjects still spawn with the host.
                Vector3 spot = garageSpot + Vector3.down * 50f + Vector3.right * (i * 6f);
                GameObject car = BuildCarBody($"Police {i + 1}", pen.transform, spot, Quaternion.identity, new Color(0.75f, 0.1f, 0.1f));

                var agent = car.AddComponent<NavMeshAgent>();
                agent.radius = 1.2f;
                agent.height = CarHeight;
                agent.baseOffset = CarHeight * 0.5f;

                car.AddComponent<NetworkObject>();
                car.AddComponent<NetworkTransform>();
                police.Add(car.AddComponent<PursuitCar>());

                car.SetActive(false);
            }

            return police.ToArray();
        }

        private static CinemachineCamera BuildChaseCamera(Transform parent, Transform car)
        {
            var camera = new GameObject("Chase Camera");
            camera.transform.SetParent(parent, false);

            CinemachineCamera cinemachine = camera.AddComponent<CinemachineCamera>();
            cinemachine.Follow = car;
            cinemachine.LookAt = car;

            // Starts below every player camera mode so it stays dormant until the director raises it.
            cinemachine.Priority = 0;

            var follow = camera.AddComponent<CinemachineFollow>();
            follow.FollowOffset = new Vector3(0f, 5f, -9f);
            follow.TrackerSettings.PositionDamping = new Vector3(1f, 1f, 1f);

            camera.AddComponent<CinemachineRotationComposer>();

            return cinemachine;
        }

        private static ExtractionZone BuildExtraction(Transform parent, Vector3 spot)
        {
            var zone = new GameObject("Extraction Point");
            zone.transform.SetParent(parent, false);
            zone.transform.position = spot;

            zone.AddComponent<NetworkObject>();
            ExtractionZone extraction = zone.AddComponent<ExtractionZone>();

            // A visible marker, since the zone itself is only a distance check.
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = "Marker";
            marker.transform.SetParent(zone.transform, false);
            marker.transform.localScale = new Vector3(6f, 0.05f, 6f);
            Object.DestroyImmediate(marker.GetComponent<Collider>());
            Paint(marker, new Color(0.2f, 1f, 0.4f));

            return extraction;
        }

        private static void BuildDirector(Transform parent, GarageDoor door, CarController car, Transform seat,
            ExtractionZone extraction, CinemachineCamera camera, PursuitCar[] police)
        {
            var director = new GameObject("Chase Director");
            director.transform.SetParent(parent, false);
            director.AddComponent<NetworkObject>();
            ChaseDirector component = director.AddComponent<ChaseDirector>();

            // Assigned through SerializedObject because the fields are private and serialized, which
            // is how they should stay: they are wiring, not API.
            var serialized = new SerializedObject(component);
            serialized.FindProperty("door").objectReferenceValue = door;
            serialized.FindProperty("playerCar").objectReferenceValue = car;
            serialized.FindProperty("driverSeat").objectReferenceValue = seat;
            serialized.FindProperty("extraction").objectReferenceValue = extraction;
            serialized.FindProperty("chaseCamera").objectReferenceValue = camera;

            SerializedProperty list = serialized.FindProperty("police");
            list.arraySize = police.Length;
            for (int i = 0; i < police.Length; i++)
            {
                list.GetArrayElementAtIndex(i).objectReferenceValue = police[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        #endregion

        #region Primitives

        private static GameObject BuildCarBody(string name, Transform parent, Vector3 spot, Quaternion facing, Color colour)
        {
            var car = new GameObject(name);
            car.transform.SetParent(parent, false);
            car.transform.SetPositionAndRotation(spot, facing);

            GameObject shell = Block(car.transform, "Body", Vector3.zero, new Vector3(CarWidth, CarHeight, CarLength));
            Object.DestroyImmediate(shell.GetComponent<Collider>());
            Paint(shell, colour);

            GameObject cabin = Block(car.transform, "Cabin", new Vector3(0f, CarHeight * 0.6f, -0.3f), new Vector3(CarWidth * 0.85f, CarHeight * 0.6f, CarLength * 0.4f));
            Object.DestroyImmediate(cabin.GetComponent<Collider>());
            Paint(cabin, colour * 0.6f);

            float axleZ = CarLength * 0.3f;
            float axleX = CarWidth * 0.5f;
            Wheel(car.transform, "Wheel FL", new Vector3(-axleX, -CarHeight * 0.4f, axleZ));
            Wheel(car.transform, "Wheel FR", new Vector3(axleX, -CarHeight * 0.4f, axleZ));
            Wheel(car.transform, "Wheel RL", new Vector3(-axleX, -CarHeight * 0.4f, -axleZ));
            Wheel(car.transform, "Wheel RR", new Vector3(axleX, -CarHeight * 0.4f, -axleZ));

            // One collider for the whole car rather than per part: a compound of boxes and cylinders
            // catches on level geometry and makes the handling impossible to reason about.
            var collider = car.AddComponent<BoxCollider>();
            collider.size = new Vector3(CarWidth, CarHeight, CarLength);

            Undo.RegisterCreatedObjectUndo(car, "Create Car");
            return car;
        }

        private static GameObject Block(Transform parent, string name, Vector3 localPosition, Vector3 size)
        {
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.transform.SetParent(parent, false);
            block.transform.localPosition = localPosition;
            block.transform.localScale = size;
            return block;
        }

        private static void Wheel(Transform parent, string name, Vector3 localPosition)
        {
            GameObject wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wheel.name = name;
            wheel.transform.SetParent(parent, false);
            wheel.transform.localPosition = localPosition;
            wheel.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            wheel.transform.localScale = new Vector3(0.6f, 0.15f, 0.6f);
            Object.DestroyImmediate(wheel.GetComponent<Collider>());
            Paint(wheel, new Color(0.1f, 0.1f, 0.12f));
        }

        private static void Paint(GameObject target, Color colour)
        {
            if (!target.TryGetComponent(out Renderer renderer))
            {
                return;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader != null)
            {
                renderer.sharedMaterial = new Material(shader) { color = colour };
            }
        }

        #endregion
    }
}
