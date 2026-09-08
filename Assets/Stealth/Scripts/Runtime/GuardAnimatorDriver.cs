using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Drives a guard's Animator so it carries, aims and fires the rifle the way the player does.
    /// </summary>
    /// <remarks>
    /// Guards run the player's own <c>ShooterAnimator</c> controller, whose UpperBody layer holds
    /// the weapon poses. That layer is what makes a guard grip the rifle in two hands and bring it
    /// up to aim; on the Core controller the arms stay at the sides and the gun floats in one hand.
    /// The controller is assigned by the Stealth setup tool.
    ///
    /// The player feeds that controller through <c>ShooterAnimator</c>, which is a NetworkAnimator
    /// requiring a CoreMovement, an AimController and a weapon event bus. A guard has none of those,
    /// so this sets the same parameters from what the guard is actually doing.
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

        [Tooltip("Which weapon pose the UpperBody layer uses. 0 is the assault rifle, matching WeaponData_AssaultRifle.")]
        [SerializeField] private int weaponTypeId;

        [Tooltip("Smoothing on the speed and strafe parameters so guards do not snap between poses.")]
        [SerializeField, Min(0f)] private float damping = 0.12f;

        private static readonly int SpeedId = Animator.StringToHash("Speed");
        private static readonly int GroundedId = Animator.StringToHash("Grounded");
        private static readonly int MotionSpeedId = Animator.StringToHash("MotionSpeed");
        private static readonly int IsAimingId = Animator.StringToHash("IsAiming");
        private static readonly int ShootId = Animator.StringToHash("Shoot");
        private static readonly int WeaponTypeId = Animator.StringToHash("WeaponTypeID");
        private static readonly int StrafeXId = Animator.StringToHash("StrafeX");
        private static readonly int StrafeYId = Animator.StringToHash("StrafeY");

        private readonly HashSet<int> m_Parameters = new HashSet<int>();
        private GuardBrain m_Brain;
        private GuardWeapon m_Weapon;
        private Vector3 m_LastPosition;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (animator == null)
            {
                animator = GetComponentInChildren<Animator>();
            }

            m_Brain = GetComponent<GuardBrain>();
            m_Weapon = GetComponent<GuardWeapon>();
            m_LastPosition = transform.position;

            CacheParameters();
            Set(WeaponTypeId, weaponTypeId);
        }

        private void OnEnable()
        {
            if (m_Weapon != null)
            {
                m_Weapon.ShotRendered += OnShotRendered;
            }
        }

        private void OnDisable()
        {
            if (m_Weapon != null)
            {
                m_Weapon.ShotRendered -= OnShotRendered;
            }
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

            float speed = delta.magnitude / Time.deltaTime;

            SetBool(GroundedId, true);
            SetFloat(MotionSpeedId, 1f);
            SetFloat(SpeedId, speed);
            SetBool(IsAimingId, IsEngaging);

            // While aiming, the controller blends locomotion by direction rather than by heading,
            // so a guard advancing on the player keeps the rifle levelled instead of turning to run.
            Vector3 local = speed > 0.01f ? transform.InverseTransformDirection(delta.normalized) : Vector3.zero;
            SetFloat(StrafeXId, local.x);
            SetFloat(StrafeYId, local.z);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// True while the guard has committed to a target, which is when the rifle should be up.
        /// </summary>
        /// <remarks>
        /// Read from the brain's replicated state so clients raise the weapon at the same moment the
        /// server does. A guard with no brain aims permanently, which is the safer default for a
        /// hand-built guard than never aiming at all.
        /// </remarks>
        private bool IsEngaging => m_Brain == null || m_Brain.State == GuardAlertState.Alerted;

        private void OnShotRendered()
        {
            if (animator != null && m_Parameters.Contains(ShootId))
            {
                animator.SetTrigger(ShootId);
            }
        }

        /// <summary>
        /// Records which parameters this guard’s controller actually declares.
        /// </summary>
        /// <remarks>
        /// Setting a parameter an Animator does not have logs "Parameter ‘Hash N’ does not exist"
        /// every frame. Guards built before the shooter controller was assigned, or built by hand,
        /// still run the Core controller, which has no IsAiming, Shoot, WeaponTypeID or Strafe
        /// parameters. Those guards should animate as best they can rather than flood the console.
        ///
        /// Cached once: swapping the controller at runtime would need this rebuilt, which the setup
        /// tool never does.
        /// </remarks>
        private void CacheParameters()
        {
            m_Parameters.Clear();

            if (animator == null || animator.runtimeAnimatorController == null)
            {
                return;
            }

            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                m_Parameters.Add(parameter.nameHash);
            }
        }

        private void Set(int id, int value)
        {
            if (animator != null && m_Parameters.Contains(id))
            {
                animator.SetInteger(id, value);
            }
        }

        private void SetBool(int id, bool value)
        {
            if (m_Parameters.Contains(id))
            {
                animator.SetBool(id, value);
            }
        }

        private void SetFloat(int id, float value)
        {
            if (m_Parameters.Contains(id))
            {
                animator.SetFloat(id, value, damping, Time.deltaTime);
            }
        }

        #endregion
    }
}
