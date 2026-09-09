using System.Collections;
using Blocks.Gameplay.Core;
using Blocks.Gameplay.Stealth;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Blocks.Gameplay.Chase
{
    /// <summary>
    /// Runs the handover from stealth to chase: the last guard falls, the garage opens, the player is
    /// put in the car, and the police arrive behind.
    /// </summary>
    /// <remarks>
    /// The whole sequence is server-driven, with only the camera cut sent to clients. Everything else
    /// is already replicated — the door through its own NetworkVariable, the cars through
    /// NetworkTransform — so re-sending it would be duplicated state that can disagree.
    ///
    /// Police cars are placed in the scene by the setup tool and parked out of sight, then moved into
    /// position when the chase starts. Spawning them from a prefab would mean registering a network
    /// prefab and instantiating NetworkObjects at runtime; scene objects spawn with the host and
    /// simply work. To the player they still appear from nowhere, which is all the fiction needs.
    ///
    /// The player is held at the driver's seat each frame rather than parented to the car. Parenting
    /// one NetworkObject to another has its own rules in Netcode and fails quietly when they are not
    /// met; setting the position of a character whose movement is switched off cannot.
    /// </remarks>
    public class ChaseDirector : NetworkBehaviour
    {
        #region Fields & Properties

        [Header("Scene pieces")]
        [Tooltip("The shutter that opens to reveal the car.")]
        [SerializeField] private GarageDoor door;

        [Tooltip("The car the player drives.")]
        [SerializeField] private CarController playerCar;

        [Tooltip("Where the player sits. Usually an empty at the car's driving position.")]
        [SerializeField] private Transform driverSeat;

        [Tooltip("Where the chase ends.")]
        [SerializeField] private ExtractionZone extraction;

        [Tooltip("Camera that takes over for driving. Its priority is raised when the chase begins.")]
        [SerializeField] private CinemachineCamera chaseCamera;

        [Tooltip("Police cars, parked out of sight until the chase starts.")]
        [SerializeField] private PursuitCar[] police;

        [Header("Timing")]
        [Tooltip("Pause after the last guard falls, before the door starts moving.")]
        [SerializeField, Min(0f)] private float beatBeforeDoor = 1.5f;

        [Tooltip("Pause after the player is seated, before the police appear.")]
        [SerializeField, Min(0f)] private float beatBeforePolice = 1f;

        [Header("Police")]
        [Tooltip("How far behind the car they appear, in metres.")]
        [SerializeField, Min(2f)] private float policeDistance = 22f;

        [Tooltip("Sideways spacing between them, in metres.")]
        [SerializeField, Min(0f)] private float policeSpread = 4f;

        [Tooltip("Priority given to the chase camera. Must beat the player's own camera modes.")]
        [SerializeField] private int chaseCameraPriority = 100;

        private CoreMovement m_Driver;
        private bool m_Started;

        #endregion

        #region Unity & Network Lifecycle

        private void Update()
        {
            if (IsServer && !m_Started && GuardBrain.AnyHaveSpawned && GuardBrain.AliveCount == 0)
            {
                m_Started = true;
                StartCoroutine(RunSequence());
            }

            // Held every frame, not once: the character controller keeps resolving collisions even
            // with movement switched off, so a single teleport would drift out of the seat.
            if (m_Driver != null && driverSeat != null)
            {
                m_Driver.SetPosition(driverSeat.position);
            }
        }

        #endregion

        #region Private Methods

        private IEnumerator RunSequence()
        {
            Debug.Log("[Chase] Last guard down. Starting the getaway.", this);

            m_Driver = FindDriver();
            if (m_Driver != null)
            {
                // Switched off before the door moves, so the player cannot walk out of their own
                // cutscene.
                m_Driver.IsMovementEnabled = false;
            }

            yield return new WaitForSeconds(beatBeforeDoor);

            if (door != null)
            {
                door.Open();
                while (!door.IsOpen)
                {
                    yield return null;
                }
            }

            SeatDriver();
            CutToChaseCameraRpc();

            yield return new WaitForSeconds(beatBeforePolice);

            DeployPolice();

            if (playerCar != null)
            {
                playerCar.HandOverTo(m_Driver != null
                    ? m_Driver.GetComponent<NetworkObject>().OwnerClientId
                    : NetworkManager.ServerClientId);
            }

            if (extraction != null && playerCar != null)
            {
                extraction.Watch(playerCar.transform);
                extraction.Reached += OnExtracted;
            }

            Debug.Log("[Chase] Drive. Reach the extraction point.", this);
        }

        /// <summary>
        /// Finds the character to put in the car.
        /// </summary>
        /// <remarks>
        /// The host's own player object, since this is built for a single player hosting. With more
        /// than one connected client this seats whoever the server reports first, which is a
        /// limitation rather than a decision.
        /// </remarks>
        private CoreMovement FindDriver()
        {
            foreach (NetworkClient client in NetworkManager.Singleton.ConnectedClientsList)
            {
                if (client.PlayerObject != null && client.PlayerObject.TryGetComponent(out CoreMovement movement))
                {
                    return movement;
                }
            }

            Debug.LogWarning("[Chase] No player object found to seat in the car.", this);
            return null;
        }

        private void SeatDriver()
        {
            if (m_Driver == null || driverSeat == null)
            {
                return;
            }

            m_Driver.SetPosition(driverSeat.position);
        }

        /// <summary>
        /// Moves the police into the mirror and sets them hunting.
        /// </summary>
        private void DeployPolice()
        {
            if (police == null || playerCar == null)
            {
                return;
            }

            Transform car = playerCar.transform;
            Vector3 behind = car.position - car.forward * policeDistance;

            for (int i = 0; i < police.Length; i++)
            {
                if (police[i] == null)
                {
                    continue;
                }

                // Fanned out sideways so they do not arrive stacked inside one another.
                float offset = (i - (police.Length - 1) * 0.5f) * policeSpread;
                Vector3 spot = behind + car.right * offset;

                police[i].transform.SetPositionAndRotation(spot, car.rotation);
                police[i].gameObject.SetActive(true);
                police[i].Chase(car);
            }

            Debug.Log($"[Chase] {police.Length} police car(s) in pursuit.", this);
        }

        private void OnExtracted()
        {
            Debug.Log("[Chase] Extraction reached. You got away.", this);
            AnnounceEscapeRpc();
        }

        [Rpc(SendTo.Everyone)]
        private void CutToChaseCameraRpc()
        {
            // Priority rather than enabling and disabling cameras: Cinemachine blends between the
            // highest-priority camera and whatever was live, so the cut comes with a move rather
            // than a jump.
            if (chaseCamera != null)
            {
                chaseCamera.Priority = chaseCameraPriority;
            }
        }

        [Rpc(SendTo.Everyone)]
        private void AnnounceEscapeRpc()
        {
            Debug.Log("[Chase] ESCAPED.");
        }

        #endregion
    }
}
