using System.Collections;
using UnityEngine;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Topples a guard over when it dies, so a kill reads as a kill rather than the guard freezing
    /// and vanishing.
    /// </summary>
    /// <remarks>
    /// This is procedural rather than a clip because the shared rig ships no death animation — the
    /// only reaction clip in the project is Armature_Shoot_HitReaction, which is a flinch. A ragdoll
    /// would need rigidbodies and joints on every bone and would then have to be reconciled with
    /// NetworkTransform, which is a great deal of machinery for a body that is removed a second later.
    ///
    /// The fall is applied to the visual child, never the guard root. The root carries the
    /// NavMeshAgent and NetworkTransform; rotating it would be replicated and would fight the
    /// server's own transform authority. Rotating the model locally means each peer plays the fall
    /// itself, driven by the replicated health value, and nothing extra goes over the wire.
    /// </remarks>
    [DisallowMultipleComponent]
    public class GuardDeathAnimation : MonoBehaviour
    {
        #region Fields & Properties

        [Tooltip("Model to topple. Found as the child named 'Visual' when left empty.")]
        [SerializeField] private Transform visual;

        [Tooltip("Seconds the guard takes to fall. Keep below GuardHealth's Despawn Delay or the body is removed mid-fall.")]
        [SerializeField, Min(0.05f)] private float duration = 0.7f;

        [Tooltip("How far the body settles into the ground as it lands, hiding the gap under a stiff model.")]
        [SerializeField, Min(0f)] private float sinkDepth = 0.25f;

        private bool m_Played;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (visual == null)
            {
                visual = transform.Find("Visual");
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Starts the fall. Safe to call more than once; only the first call does anything.
        /// </summary>
        /// <remarks>
        /// Guarded because health can be written more than once in a frame, and because a late
        /// joiner receives the replicated zero as a value change of its own.
        /// </remarks>
        public void Play()
        {
            if (m_Played || visual == null)
            {
                return;
            }

            m_Played = true;
            StartCoroutine(Fall());
        }

        #endregion

        #region Private Methods

        private IEnumerator Fall()
        {
            // The locomotion state would keep driving the bones and undo the rotation every frame.
            Animator animator = visual.GetComponentInChildren<Animator>();
            if (animator != null)
            {
                animator.enabled = false;
            }

            Quaternion startRotation = visual.localRotation;
            Quaternion endRotation = Quaternion.AngleAxis(90f, Vector3.right) * startRotation;
            Vector3 startPosition = visual.localPosition;
            Vector3 endPosition = startPosition - new Vector3(0f, sinkDepth, 0f);

            for (float elapsed = 0f; elapsed < duration; elapsed += Time.deltaTime)
            {
                float t = Mathf.Clamp01(elapsed / duration);

                // Accelerating fall: a linear topple looks like the guard is being lowered on a wire.
                float eased = t * t;

                visual.localRotation = Quaternion.Slerp(startRotation, endRotation, eased);
                visual.localPosition = Vector3.Lerp(startPosition, endPosition, eased);
                yield return null;
            }

            visual.localRotation = endRotation;
            visual.localPosition = endPosition;
        }

        #endregion
    }
}
