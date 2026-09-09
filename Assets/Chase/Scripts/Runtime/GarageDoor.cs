using Unity.Netcode;
using UnityEngine;

namespace Blocks.Gameplay.Chase
{
    /// <summary>
    /// A shutter that slides up out of the way once, when the chase sequence calls for it.
    /// </summary>
    /// <remarks>
    /// Driven by a replicated open fraction rather than an animation clip, so a client that joins
    /// mid-open sees the door at the right height instead of snapping or replaying from the start.
    /// </remarks>
    public class GarageDoor : NetworkBehaviour
    {
        #region Fields & Properties

        [Tooltip("How far the door travels upward, in metres. Should clear the car.")]
        [SerializeField, Min(0f)] private float travel = 4f;

        [Tooltip("Seconds the door takes to open fully.")]
        [SerializeField, Min(0.1f)] private float openDuration = 2.5f;

        private readonly NetworkVariable<float> m_Openness = new NetworkVariable<float>(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private Vector3 m_ClosedPosition;
        private bool m_Opening;

        /// <summary>True once the door has finished travelling.</summary>
        public bool IsOpen => m_Openness.Value >= 1f;

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            m_ClosedPosition = transform.localPosition;
        }

        private void Update()
        {
            if (IsServer && m_Opening && m_Openness.Value < 1f)
            {
                m_Openness.Value = Mathf.MoveTowards(m_Openness.Value, 1f, Time.deltaTime / openDuration);
            }

            // Applied on every peer from the replicated value, so the door is in the same place for
            // everyone without sending a transform update per frame.
            transform.localPosition = m_ClosedPosition + Vector3.up * (travel * Smooth(m_Openness.Value));
        }

        #endregion

        #region Public Methods

        /// <summary>Starts the door opening. Server only; ignored if already opening.</summary>
        public void Open()
        {
            if (IsServer)
            {
                m_Opening = true;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>Eases the ends so the shutter does not start and stop dead.</summary>
        private static float Smooth(float t) => t * t * (3f - 2f * t);

        #endregion
    }
}
