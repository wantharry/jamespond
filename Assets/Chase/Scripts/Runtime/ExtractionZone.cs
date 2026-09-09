using System;
using Unity.Netcode;
using UnityEngine;

namespace Blocks.Gameplay.Chase
{
    /// <summary>
    /// The place the chase ends. Driving the car into it wins the game.
    /// </summary>
    /// <remarks>
    /// Tested by distance on the server rather than with a trigger collider. The car is moving fast
    /// enough that a thin trigger can be stepped over entirely between physics frames, and a missed
    /// win condition is the worst possible bug to debug.
    /// </remarks>
    public class ExtractionZone : NetworkBehaviour
    {
        #region Fields & Properties

        [Tooltip("How close the car must get, in metres.")]
        [SerializeField, Min(1f)] private float radius = 6f;

        private Transform m_Target;
        private bool m_Reached;

        /// <summary>Raised on the server the moment the car arrives.</summary>
        public event Action Reached;

        #endregion

        #region Unity Lifecycle

        private void Update()
        {
            if (!IsServer || m_Reached || m_Target == null)
            {
                return;
            }

            if (Vector3.Distance(m_Target.position, transform.position) > radius)
            {
                return;
            }

            m_Reached = true;
            Reached?.Invoke();
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, radius);
        }

        #endregion

        #region Public Methods

        /// <summary>Starts watching for the given car to arrive.</summary>
        public void Watch(Transform car)
        {
            m_Target = car;
            m_Reached = false;
        }

        #endregion
    }
}
