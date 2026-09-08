using Blocks.Gameplay.Core;
using UnityEngine;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Pure sensing component for a guard. Answers one question each tick: "can I see a player
    /// right now, and how strongly?" It holds no state machine and drives no movement — that is
    /// <see cref="GuardBrain"/>'s job. Keeping the two apart means detection can be tuned and
    /// visualised on its own.
    /// </summary>
    /// <remarks>
    /// Detection is a cone test, not a sphere: the target must be inside <see cref="viewRadius"/>,
    /// within <see cref="viewAngle"/> of the guard's facing, and unobstructed by geometry on
    /// <see cref="obstacleMask"/>. Visibility strength falls off with distance so a player at the
    /// far edge of the cone is spotted slowly and one at point-blank range almost instantly.
    /// </remarks>
    [DisallowMultipleComponent]
    public class GuardVision : MonoBehaviour
    {
        #region Fields & Properties

        [Header("Cone")]
        [Tooltip("How far the guard can see, in metres.")]
        [SerializeField, Min(0f)] private float viewRadius = 14f;

        [Tooltip("Total width of the vision cone in degrees (centred on the guard's forward axis).")]
        [SerializeField, Range(0f, 360f)] private float viewAngle = 100f;

        [Tooltip("Height above the guard's pivot that sight lines originate from, i.e. roughly eye level.")]
        [SerializeField, Min(0f)] private float eyeHeight = 1.6f;

        [Header("Layers")]
        [Tooltip("Layers that can be detected. Put the player on one of these.")]
        [SerializeField] private LayerMask targetMask = ~0;

        [Tooltip("Layers that block line of sight. Walls and level geometry belong here; the player must NOT.")]
        [SerializeField] private LayerMask obstacleMask = ~0;

        [Header("Falloff")]
        [Tooltip("Visibility strength at the very edge of the view radius. 1 means distance does not matter.")]
        [SerializeField, Range(0f, 1f)] private float strengthAtMaxRange = 0.25f;

        [Tooltip("Vertical offset on the target that the guard aims its sight line at, so it looks at the torso rather than the feet.")]
        [SerializeField, Min(0f)] private float targetCentreOffset = 1.0f;

        /// <summary>
        /// World-space origin of the guard's sight lines.
        /// </summary>
        public Vector3 EyePosition => transform.position + Vector3.up * eyeHeight;

        /// <summary>
        /// How far this guard can see. Read by <see cref="GuardBrain"/> for search behaviour.
        /// </summary>
        public float ViewRadius => viewRadius;

        /// <summary>
        /// Total cone width in degrees.
        /// </summary>
        public float ViewAngle => viewAngle;

        /// <summary>What blocks sight. Exposed so a visualisation can clip itself the same way.</summary>
        public LayerMask ObstacleMask => obstacleMask;

        /// <summary>
        /// The transform this guard could see on the most recent <see cref="Scan"/>, or null.
        /// Retained purely so gizmos can draw the live sight line.
        /// </summary>
        public Transform LastSeenTarget { get; private set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Tests for a visible player and reports how strongly it is seen.
        /// </summary>
        /// <param name="target">The closest visible player, or null when nothing is in view.</param>
        /// <param name="strength">
        /// Visibility in the range 0..1, scaled by distance. 0 when nothing is visible, approaching
        /// 1 at point-blank range. Multiply detection rate by this so distant targets fill awareness
        /// slowly.
        /// </param>
        /// <returns>True when a player is visible this tick.</returns>
        public bool Scan(out Transform target, out float strength)
        {
            target = null;
            strength = 0f;

            // Broad phase: everything on the target layers within reach.
            Collider[] candidates = Physics.OverlapSphere(EyePosition, viewRadius, targetMask);
            if (candidates.Length == 0)
            {
                LastSeenTarget = null;
                return false;
            }

            float bestSqrDistance = float.MaxValue;

            for (int i = 0; i < candidates.Length; i++)
            {
                Collider candidate = candidates[i];

                // Only players count as detectable, and only while they are alive. A dead or
                // despawned player should not keep a guard alerted.
                CorePlayerState playerState = candidate.GetComponentInParent<CorePlayerState>();
                if (playerState == null || !playerState.IsActive)
                {
                    continue;
                }

                Vector3 targetPoint = candidate.transform.position + Vector3.up * targetCentreOffset;
                if (!HasLineOfSight(targetPoint, out float distance))
                {
                    continue;
                }

                // Keep the nearest visible player; a guard reacts to the closest threat.
                float sqrDistance = distance * distance;
                if (sqrDistance < bestSqrDistance)
                {
                    bestSqrDistance = sqrDistance;
                    target = playerState.transform;

                    // A crouching player is still seen, but registers far more slowly. That is what
                    // makes crouch a stealth action rather than just a slower walk.
                    strength = StrengthAtDistance(distance) * VisibilityOf(playerState);
                }
            }

            LastSeenTarget = target;
            return target != null;
        }

        /// <summary>
        /// Tests whether a specific world point is inside the cone and unobstructed. Exposed so the
        /// brain can re-check a remembered position without running a full scan.
        /// </summary>
        /// <param name="worldPoint">The point to test.</param>
        /// <param name="distance">Distance from the guard's eye to that point.</param>
        /// <returns>True when the point is visible.</returns>
        public bool HasLineOfSight(Vector3 worldPoint, out float distance)
        {
            Vector3 eye = EyePosition;
            Vector3 toTarget = worldPoint - eye;
            distance = toTarget.magnitude;

            if (distance > viewRadius || distance <= Mathf.Epsilon)
            {
                return false;
            }

            Vector3 direction = toTarget / distance;

            // Cone test. viewAngle is the full width, so compare against half of it.
            float angleToTarget = Vector3.Angle(transform.forward, direction);
            if (angleToTarget > viewAngle * 0.5f)
            {
                return false;
            }

            // Occlusion test. QueryTriggerInteraction.Ignore keeps trigger volumes
            // (spawn pads, pickups, trigger zones) from counting as walls.
            if (Physics.Raycast(eye, direction, distance, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            return true;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// How visible a given player currently is, as a multiplier on detection strength. Standing
        /// players return 1; crouching ones return whatever their <see cref="PlayerCrouch"/> is
        /// configured to expose.
        /// </summary>
        /// <remarks>
        /// Looked up per scan rather than cached, because a player can stand or crouch at any time
        /// and a stale multiplier would let someone break line of sight while crouched and stay
        /// "hidden" after standing back up.
        /// </remarks>
        private float VisibilityOf(CorePlayerState playerState)
        {
            PlayerCrouch crouch = playerState.GetComponent<PlayerCrouch>();
            return crouch != null ? crouch.VisibilityMultiplier : 1f;
        }

        /// <summary>
        /// Maps distance to a 0..1 visibility multiplier, lerping from 1 at the guard's feet down to
        /// <see cref="strengthAtMaxRange"/> at the edge of the cone.
        /// </summary>
        private float StrengthAtDistance(float distance)
        {
            if (viewRadius <= Mathf.Epsilon)
            {
                return 0f;
            }

            float normalised = Mathf.Clamp01(distance / viewRadius);
            return Mathf.Lerp(1f, strengthAtMaxRange, normalised);
        }

        #endregion

        #region Gizmos

        /// <summary>
        /// Draws the vision cone in the Scene view when the guard is selected. Tuning detection
        /// without seeing the cone is guesswork, so this is deliberately always available rather
        /// than sitting behind an editor-only assembly.
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            Vector3 eye = EyePosition;

            // Range ring.
            Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.35f);
            DrawWireArc(eye, viewRadius, 360f, 48);

            // Cone edges and fill.
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.9f);
            Vector3 left = DirectionFromAngle(-viewAngle * 0.5f);
            Vector3 right = DirectionFromAngle(viewAngle * 0.5f);
            Gizmos.DrawLine(eye, eye + left * viewRadius);
            Gizmos.DrawLine(eye, eye + right * viewRadius);
            DrawWireArc(eye, viewRadius, viewAngle, 24);

            // Live sight line to whatever is currently seen.
            if (LastSeenTarget != null)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawLine(eye, LastSeenTarget.position + Vector3.up * targetCentreOffset);
            }
        }

        /// <summary>
        /// Converts an angle offset from the guard's forward direction into a world-space direction.
        /// </summary>
        private Vector3 DirectionFromAngle(float degreesFromForward)
        {
            float yaw = transform.eulerAngles.y + degreesFromForward;
            return new Vector3(Mathf.Sin(yaw * Mathf.Deg2Rad), 0f, Mathf.Cos(yaw * Mathf.Deg2Rad));
        }

        /// <summary>
        /// Draws a horizontal arc centred on the guard's facing, used for both the cone and the
        /// full range ring.
        /// </summary>
        private void DrawWireArc(Vector3 centre, float radius, float totalAngle, int segments)
        {
            if (segments < 1 || radius <= 0f)
            {
                return;
            }

            float step = totalAngle / segments;
            Vector3 previous = centre + DirectionFromAngle(-totalAngle * 0.5f) * radius;

            for (int i = 1; i <= segments; i++)
            {
                Vector3 next = centre + DirectionFromAngle(-totalAngle * 0.5f + step * i) * radius;
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
        }

        #endregion
    }
}
