using UnityEngine;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Poses the character into a crouch by lowering its hips and pinning its feet, letting Unity's
    /// humanoid solver bend the knees.
    /// </summary>
    /// <remarks>
    /// There is no crouch clip in this project, and none of the shipped animation covers a crouch.
    /// Rather than import one, this uses the fact that the rig is Humanoid with a full bone map: drop
    /// <c>bodyPosition</c>, hold each foot at the spot the animation had already placed it, and the
    /// IK solver produces the knee bend for free. It composes with whatever locomotion is playing, so
    /// the character crouch-walks without a separate crouched walk clip existing.
    ///
    /// Feet are captured before the hips move, not after. Reading them afterwards would return
    /// positions already dragged down by the dip, so the solver would have nothing to correct and the
    /// character would sink through the floor instead of bending.
    ///
    /// This lives on the Animator's own GameObject because <c>OnAnimatorIK</c> is only called there,
    /// while <see cref="PlayerCrouch"/> sits on the player root with the controller and the network
    /// state — hence the search up the parents.
    ///
    /// Requires the IK Pass checkbox on the animator layer. Without it Unity never calls
    /// <c>OnAnimatorIK</c> and this silently does nothing.
    /// </remarks>
    [RequireComponent(typeof(Animator))]
    public class CrouchIK : MonoBehaviour
    {
        #region Fields & Properties

        [Tooltip("How far the hips drop at full crouch, in metres. Roughly the collider's height loss.")]
        [SerializeField, Min(0f)] private float hipDrop = 0.45f;

        [Tooltip("Forward lean at full crouch, in degrees. A little reads as bracing rather than squatting.")]
        [SerializeField, Range(0f, 45f)] private float forwardLean = 12f;

        [Tooltip("How quickly the pose follows the crouch state. Should roughly match PlayerCrouch's Height Change Speed.")]
        [SerializeField, Min(0.1f)] private float blendSpeed = 6f;

        private Animator m_Animator;
        private PlayerCrouch m_Crouch;
        private float m_Weight;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            m_Animator = GetComponent<Animator>();
            m_Crouch = GetComponentInParent<PlayerCrouch>();
        }

        private void Update()
        {
            // Blended rather than switched, so standing up is a movement and not a snap. Normalised
            // against hipDrop so the eased metres match PlayerCrouch's own collider easing.
            float target = m_Crouch != null && m_Crouch.IsCrouching ? 1f : 0f;
            m_Weight = Mathf.MoveTowards(m_Weight, target, blendSpeed * Time.deltaTime / Mathf.Max(hipDrop, 0.01f));
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (m_Animator == null || m_Weight <= 0f)
            {
                return;
            }

            Transform leftFoot = m_Animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform rightFoot = m_Animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (leftFoot == null || rightFoot == null)
            {
                return;
            }

            // Captured first: once the hips drop, these have moved with them.
            Vector3 leftPosition = leftFoot.position;
            Quaternion leftRotation = leftFoot.rotation;
            Vector3 rightPosition = rightFoot.position;
            Quaternion rightRotation = rightFoot.rotation;

            m_Animator.bodyPosition -= Vector3.up * (hipDrop * m_Weight);
            m_Animator.bodyRotation = Quaternion.AngleAxis(forwardLean * m_Weight, m_Animator.transform.right)
                                      * m_Animator.bodyRotation;

            Pin(AvatarIKGoal.LeftFoot, leftPosition, leftRotation);
            Pin(AvatarIKGoal.RightFoot, rightPosition, rightRotation);
        }

        #endregion

        #region Private Methods

        private void Pin(AvatarIKGoal goal, Vector3 position, Quaternion rotation)
        {
            m_Animator.SetIKPositionWeight(goal, m_Weight);
            m_Animator.SetIKRotationWeight(goal, m_Weight);
            m_Animator.SetIKPosition(goal, position);
            m_Animator.SetIKRotation(goal, rotation);
        }

        #endregion
    }
}
