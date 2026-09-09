using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Blocks.Gameplay.Chase
{
    /// <summary>
    /// A police car that hunts the player's car across the NavMesh.
    /// </summary>
    /// <remarks>
    /// Steered by a NavMeshAgent rather than by the same arcade physics the player's car uses. A
    /// pursuer driven by forces has to be taught to brake for corners and to not understeer into
    /// walls, which is a driving AI problem; a NavMeshAgent already knows the route. The car is
    /// rotated to face its own travel direction so it still reads as driving rather than sliding.
    ///
    /// It reuses the level's existing NavMesh, the one baked for the guards, so it can only chase
    /// where a guard could walk. That is the honest limit of this approach: no roads, no racing
    /// line, just relentless pursuit through walkable space.
    ///
    /// Server only. Clients see the result through NetworkTransform.
    /// </remarks>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkObject))]
    public class PursuitCar : NetworkBehaviour
    {
        #region Fields & Properties

        [Tooltip("Top speed in metres per second. Slightly under the player's so a clean run escapes.")]
        [SerializeField, Min(1f)] private float chaseSpeed = 22f;

        [Tooltip("How hard it accelerates. Low values let the player win the launch off the line.")]
        [SerializeField, Min(1f)] private float acceleration = 12f;

        [Tooltip("Degrees per second the body turns to face its travel direction.")]
        [SerializeField, Min(1f)] private float turnRate = 220f;

        [Tooltip("Seconds between repaths. Every frame is wasted work; too slow and it cuts corners late.")]
        [SerializeField, Min(0.05f)] private float repathInterval = 0.25f;

        private NavMeshAgent m_Agent;
        private Transform m_Quarry;
        private float m_RepathTimer;

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            m_Agent = GetComponent<NavMeshAgent>();
            m_Agent.speed = chaseSpeed;
            m_Agent.acceleration = acceleration;
            m_Agent.angularSpeed = 0f;

            // The agent moves the car; the body's facing is handled here so it banks into its own
            // direction of travel. Letting the agent rotate as well fights that and looks like a
            // spinning top at speed.
            m_Agent.updateRotation = false;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // Only the server pathfinds. A client running its own agent would fight the replicated
            // transform, exactly as the guards do.
            if (!IsServer && m_Agent != null)
            {
                m_Agent.enabled = false;
            }
        }

        private void Update()
        {
            if (!IsServer || m_Quarry == null || m_Agent == null || !m_Agent.isOnNavMesh)
            {
                return;
            }

            m_RepathTimer -= Time.deltaTime;
            if (m_RepathTimer <= 0f)
            {
                m_RepathTimer = repathInterval;
                m_Agent.SetDestination(m_Quarry.position);
            }

            FaceTravelDirection();
        }

        #endregion

        #region Public Methods

        /// <summary>Sets the car this one chases.</summary>
        public void Chase(Transform quarry)
        {
            m_Quarry = quarry;
            m_RepathTimer = 0f;
        }

        #endregion

        #region Private Methods

        private void FaceTravelDirection()
        {
            Vector3 heading = m_Agent.velocity;
            heading.y = 0f;

            if (heading.sqrMagnitude < 0.01f)
            {
                return;
            }

            Quaternion desired = Quaternion.LookRotation(heading.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, desired, turnRate * Time.deltaTime);
        }

        #endregion
    }
}
