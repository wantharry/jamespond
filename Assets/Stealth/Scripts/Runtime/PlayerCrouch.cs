using Blocks.Gameplay.Core;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Crouching, as an <see cref="IMovementAbility"/> on the player. Slows movement, shrinks the
    /// character's collider, blocks sprinting, and makes the player materially harder for
    /// <see cref="GuardVision"/> to detect.
    /// </summary>
    /// <remarks>
    /// Crouch state is replicated because detection runs on the server: a guard has to know the
    /// player is crouched to apply the visibility penalty, and a client-only bool would leave the
    /// server evaluating a standing player.
    ///
    /// The base kit defines <c>MovementModifier.ControllerHeight</c> but never reads it, so this
    /// resizes the <see cref="CharacterController"/> directly rather than returning a height request
    /// that nothing would honour.
    ///
    /// Input is read straight from the keyboard rather than through the shared
    /// <c>GameplayInputSystem_Actions</c> asset. Adding a binding there would mean regenerating the
    /// committed C# wrapper in Core, and this keeps the stealth module self-contained.
    /// </remarks>
    [RequireComponent(typeof(CoreMovement))]
    [DisallowMultipleComponent]
    public class PlayerCrouch : NetworkBehaviour, IMovementAbility
    {
        #region Fields & Properties

        [Header("Input")]
        [Tooltip("Key that crouches. Q by default.")]
        [SerializeField] private Key crouchKey = Key.Q;

        [Tooltip("Tap to toggle crouch instead of holding the key down.")]
        [SerializeField] private bool toggleMode;

        [Header("Movement")]
        [Tooltip("Movement speed while crouched. The standing speed is whatever CoreMovement is configured with.")]
        [SerializeField, Min(0f)] private float crouchSpeed = 1.6f;

        [Header("Collider")]
        [Tooltip("CharacterController height while crouched.")]
        [SerializeField, Min(0.2f)] private float crouchHeight = 1.0f;

        [Tooltip("How fast the collider resizes, in metres per second. Instant resizing pops the camera.")]
        [SerializeField, Min(0.1f)] private float heightChangeSpeed = 6f;

        [Header("Stealth")]
        [Tooltip("How visible the player is to guards while crouched, as a fraction. 0.4 means awareness fills at 40% the normal rate.")]
        [SerializeField, Range(0.05f, 1f)] private float crouchedVisibility = 0.4f;

        [Header("Headroom")]
        [Tooltip("Layers that can block standing up. Level geometry belongs here; the player must not.")]
        [SerializeField] private LayerMask headroomMask = 1;

        private CoreMovement m_Motor;
        private CharacterController m_Controller;

        private float m_StandingHeight;
        private Vector3 m_StandingCenter;
        private float m_StandingMoveSpeed;
        private bool m_CapturedStandingPose;

        /// <summary>
        /// Replicated crouch state. The owner drives it from input; the server reads it for
        /// detection and every peer reads it to size the collider.
        /// </summary>
        private readonly NetworkVariable<bool> m_IsCrouching = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        /// <summary>
        /// Whether this player is currently crouched. Safe to read on any peer.
        /// </summary>
        public bool IsCrouching => m_IsCrouching.Value;

        /// <summary>
        /// Visibility multiplier this player currently presents to guards. 1 while standing.
        /// </summary>
        public float VisibilityMultiplier => m_IsCrouching.Value ? crouchedVisibility : 1f;

        /// <summary>
        /// Processed before <see cref="WalkAbility"/> (priority 0) so the speed cap it applies is
        /// already in place when walking reads it.
        /// </summary>
        public int Priority => 5;

        /// <summary>
        /// Crouching is a sustained stance rather than a burst action, so it costs no stamina.
        /// </summary>
        public float StaminaCost => 0f;

        #endregion

        #region Unity & Netcode Methods

        private void Awake()
        {
            m_Motor = GetComponent<CoreMovement>();
            m_Controller = GetComponent<CharacterController>();
            CaptureStandingPose();
        }

        private void Update()
        {
            // Only the owning client reads input; everyone else follows the replicated state.
            if (IsOwner)
            {
                ReadCrouchInput();
            }

            ApplyControllerHeight();
        }

        #endregion

        #region IMovementAbility Implementation

        /// <summary>
        /// Caches the motor and its configured standing speed.
        /// </summary>
        public void Initialize(CoreMovement movementController)
        {
            m_Motor = movementController;
            CaptureStandingPose();
        }

        /// <summary>
        /// Applies the crouch speed cap and suppresses sprinting. Contributes no velocity of its
        /// own; walking still does the moving.
        /// </summary>
        public MovementModifier Process()
        {
            if (m_Motor == null)
            {
                return default;
            }

            if (m_IsCrouching.Value)
            {
                m_Motor.moveSpeed = crouchSpeed;

                // You cannot sprint from a crouch. Cleared every frame because the input handler
                // sets it independently.
                m_Motor.SetSprintState(false);
            }
            else
            {
                m_Motor.moveSpeed = m_StandingMoveSpeed;
            }

            // Height is driven in Update so it keeps easing even when movement is disabled.
            return default;
        }

        /// <summary>
        /// Toggles the crouch. Exposed so other systems (a takedown, a scripted beat) can force the
        /// stance without simulating a key press.
        /// </summary>
        /// <returns>True if the stance changed.</returns>
        public bool TryActivate()
        {
            return SetCrouched(!m_IsCrouching.Value);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Requests a crouch state change, refusing to stand up under an obstruction.
        /// </summary>
        /// <param name="crouched">Desired stance.</param>
        /// <returns>True if the stance changed.</returns>
        public bool SetCrouched(bool crouched)
        {
            if (!IsOwner || crouched == m_IsCrouching.Value)
            {
                return false;
            }

            // Standing up inside a vent or under a low ceiling would push the collider through
            // geometry, so the stance is held until there is room.
            if (!crouched && !HasHeadroom())
            {
                return false;
            }

            m_IsCrouching.Value = crouched;
            return true;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Records the standing collider dimensions and speed once, so crouching can restore them.
        /// </summary>
        private void CaptureStandingPose()
        {
            if (m_CapturedStandingPose || m_Controller == null)
            {
                return;
            }

            m_StandingHeight = m_Controller.height;
            m_StandingCenter = m_Controller.center;
            m_StandingMoveSpeed = m_Motor != null ? m_Motor.moveSpeed : 4f;
            m_CapturedStandingPose = true;
        }

        /// <summary>
        /// Turns key state into crouch requests, in hold or toggle mode.
        /// </summary>
        private void ReadCrouchInput()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            KeyControl key = keyboard[crouchKey];
            if (key == null)
            {
                return;
            }

            if (toggleMode)
            {
                if (key.wasPressedThisFrame)
                {
                    SetCrouched(!m_IsCrouching.Value);
                }
                return;
            }

            if (key.isPressed && !m_IsCrouching.Value)
            {
                SetCrouched(true);
            }
            else if (!key.isPressed && m_IsCrouching.Value)
            {
                // May be refused while there is no headroom; the next frame retries.
                SetCrouched(false);
            }
        }

        /// <summary>
        /// Eases the CharacterController between standing and crouched dimensions.
        /// </summary>
        /// <remarks>
        /// The centre is kept at half the height so the capsule shrinks toward the feet rather than
        /// sinking the player through the floor.
        /// </remarks>
        private void ApplyControllerHeight()
        {
            if (m_Controller == null || !m_CapturedStandingPose)
            {
                return;
            }

            float targetHeight = m_IsCrouching.Value ? crouchHeight : m_StandingHeight;
            if (Mathf.Approximately(m_Controller.height, targetHeight))
            {
                return;
            }

            float newHeight = Mathf.MoveTowards(m_Controller.height, targetHeight, heightChangeSpeed * Time.deltaTime);
            m_Controller.height = newHeight;

            // Scale the centre with the height, preserving the standing offset ratio.
            float ratio = m_StandingHeight > Mathf.Epsilon ? newHeight / m_StandingHeight : 1f;
            m_Controller.center = new Vector3(
                m_StandingCenter.x,
                m_StandingCenter.y * ratio,
                m_StandingCenter.z);
        }

        /// <summary>
        /// Tests whether there is room to stand back up.
        /// </summary>
        private bool HasHeadroom()
        {
            if (m_Controller == null)
            {
                return true;
            }

            // Cast from the top of the crouched capsule up to where the standing head would be.
            Vector3 feet = transform.position + m_Controller.center - Vector3.up * (m_Controller.height * 0.5f);
            Vector3 castOrigin = feet + Vector3.up * (m_Controller.radius + 0.05f);
            float castDistance = m_StandingHeight - m_Controller.radius * 2f;

            if (castDistance <= 0f)
            {
                return true;
            }

            return !Physics.SphereCast(
                castOrigin,
                m_Controller.radius * 0.95f,
                Vector3.up,
                out _,
                castDistance,
                headroomMask,
                QueryTriggerInteraction.Ignore);
        }

        #endregion
    }
}
