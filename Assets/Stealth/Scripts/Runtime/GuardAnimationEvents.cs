using UnityEngine;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Absorbs the animation events baked into the shared player locomotion clips.
    /// </summary>
    /// <remarks>
    /// The clips call OnFootstepWalk, OnFootstepRun and OnLand. On the player those land on
    /// <c>CoreAnimator</c>, which guards deliberately do not have — it requires a CoreMovement and
    /// is a NetworkAnimator. Unity logs "has no receiver!" once per event per guard when nothing
    /// answers, which floods the console during a patrol.
    ///
    /// The methods are intentionally empty. Guard footstep audio would be a real stealth feature —
    /// hearing a patrol before seeing it — but it needs sound assets wired per guard, so this only
    /// silences the warning and marks where that would hook in.
    /// </remarks>
    public class GuardAnimationEvents : MonoBehaviour
    {
        #region Animation Events

        public void OnFootstepWalk(AnimationEvent animationEvent)
        {
        }

        public void OnFootstepRun(AnimationEvent animationEvent)
        {
        }

        public void OnLand(AnimationEvent animationEvent)
        {
        }

        #endregion
    }
}
