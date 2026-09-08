using UnityEngine;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Drives a guard's Animator from how far the guard actually moved, so guards walk and idle
    /// using the same animation controller as the player.
    /// </summary>
    /// <remarks>
    /// The player feeds that controller through <c>CoreAnimator</c>, which requires a CoreMovement
    /// and is a NetworkAnimator. Guards have neither, so reusing it would log an error per guard on
    /// spawn and sync animator state we do not need.
    ///
    /// Speed comes from the transform delta rather than NavMeshAgent.velocity because only the
    /// server runs the agent. On a client the guard is moved by NetworkTransform, leaving the
    /// agent's velocity at zero and every guard sliding around in an idle pose.
    /// </remarks>
    public class GuardAnimatorDriver : MonoBehaviour
    {
        #region Fields & Properties

        [Tooltip("Animator on the guard's visual model. Found in children when left empty.")]
        [SerializeField] private Animator animator;

        [Tooltip("Smoothing on the speed parameter so guards do not snap between poses.")]
        [SerializeField, Min(0f)] private float speedDamping = 0.12f;

        private static readonly int SpeedId = Animator.StringToHash("Speed");
        private static readonly int GroundedId = Animator.StringToHash("Grounded");
        private static readonly int MotionSpeedId = Animator.StringToHash("MotionSpeed");

        private Vector3 m_LastPosition;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (animator == null)
            {
                animator = GetComponentInChildren<Animator>();
            }

            m_LastPosition = transform.position;
        }

        private void Update()
        {
            if (animator == null || Time.deltaTime <= 0f)
            {
                return;
            }

            // Horizontal only: vertical settling onto the navmesh is not locomotion and would
            // otherwise read as the guard breaking into a walk while standing still.
            Vector3 delta = transform.position - m_LastPosition;
            delta.y = 0f;
            m_LastPosition = transform.position;

            animator.SetBool(GroundedId, true);
            animator.SetFloat(MotionSpeedId, 1f);
            animator.SetFloat(SpeedId, delta.magnitude / Time.deltaTime, speedDamping, Time.deltaTime);
        }

        #endregion
    }
}
