using Blocks.Gameplay.Core;
using Blocks.Gameplay.Shooter;
using Unity.Netcode;
using UnityEngine;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Makes the player audible. Moving, landing and firing all give guards something to hear, with
    /// how far it carries depending on what you are doing.
    /// </summary>
    /// <remarks>
    /// Loudness is a radius in metres so it can be reasoned about directly: crouching barely carries
    /// past arm's reach, walking carries across a room, sprinting and gunfire carry across the level.
    /// That ordering is the whole point — it is what makes crouching worth the speed penalty and
    /// sprinting a decision rather than a default.
    ///
    /// Server only. Guards run on the server, and the player's position arrives there through
    /// NetworkTransform, so noise is emitted where it will actually be heard. Emitting on the owning
    /// client instead would do nothing on a remote host.
    ///
    /// Movement noise is emitted on a fixed cadence rather than every frame, so it behaves like
    /// footfalls rather than a continuous siren, and so a guard's awareness climbs in steps you can
    /// react to.
    /// </remarks>
    [DisallowMultipleComponent]
    public class PlayerNoise : NetworkBehaviour
    {
        #region Fields & Properties

        [Header("How far it carries, in metres")]
        [Tooltip("Crouched movement. The quietest thing you can do while still moving.")]
        [SerializeField, Min(0f)] private float crouchRadius = 3f;

        [Tooltip("Ordinary walking.")]
        [SerializeField, Min(0f)] private float walkRadius = 9f;

        [Tooltip("Sprinting. As loud as gunfire, so running past a guard is never safe.")]
        [SerializeField, Min(0f)] private float sprintRadius = 25f;

        [Tooltip("Landing after a fall.")]
        [SerializeField, Min(0f)] private float landingRadius = 14f;

        [Tooltip("Firing a weapon. The loudest thing in the game.")]
        [SerializeField, Min(0f)] private float gunshotRadius = 25f;

        [Header("Timing")]
        [Tooltip("Seconds between footfall noises while moving.")]
        [SerializeField, Min(0.05f)] private float stepInterval = 0.45f;

        [Tooltip("Speed below which the player counts as standing still and makes no noise at all.")]
        [SerializeField, Min(0f)] private float stillSpeedThreshold = 0.2f;

        private CoreMovement m_Motor;
        private PlayerCrouch m_Crouch;
        private Vector3 m_LastPosition;
        private float m_StepTimer;
        private bool m_WasGrounded = true;

        #endregion

        #region Network Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            m_Motor = GetComponent<CoreMovement>();
            m_Crouch = GetComponent<PlayerCrouch>();
            m_LastPosition = transform.position;

            if (IsServer)
            {
                ModularWeapon.AnyWeaponFired += OnWeaponFired;
            }
        }

        public override void OnNetworkDespawn()
        {
            ModularWeapon.AnyWeaponFired -= OnWeaponFired;
            base.OnNetworkDespawn();
        }

        #endregion

        #region Unity Lifecycle

        private void Update()
        {
            if (!IsServer || Time.deltaTime <= 0f)
            {
                return;
            }

            // Horizontal only: falling is not footsteps, and the landing is reported separately.
            Vector3 delta = transform.position - m_LastPosition;
            delta.y = 0f;
            m_LastPosition = transform.position;

            ReportLanding();

            float speed = delta.magnitude / Time.deltaTime;
            if (speed < stillSpeedThreshold)
            {
                // Standing still is genuinely silent, which is what gives holding position a point.
                m_StepTimer = 0f;
                return;
            }

            m_StepTimer -= Time.deltaTime;
            if (m_StepTimer > 0f)
            {
                return;
            }

            m_StepTimer = stepInterval;
            GuardBrain.BroadcastNoise(transform.position, RadiusForSpeed(speed));
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Picks how far the current movement carries.
        /// </summary>
        /// <remarks>
        /// Crouching wins outright over speed: <see cref="PlayerCrouch"/> already caps movement, so a
        /// crouching player can never be moving fast enough to be classed as sprinting anyway, and
        /// testing the stance first keeps the intent obvious.
        /// </remarks>
        private float RadiusForSpeed(float speed)
        {
            if (m_Crouch != null && m_Crouch.IsCrouching)
            {
                return crouchRadius;
            }

            // Halfway between the two configured speeds, so the classification does not flicker at
            // the exact moment the motor is accelerating between them.
            float sprintCutoff = m_Motor == null
                ? 5f
                : Mathf.Lerp(m_Motor.moveSpeed, m_Motor.sprintSpeed, 0.5f);

            return speed >= sprintCutoff ? sprintRadius : walkRadius;
        }

        /// <summary>
        /// Reports the thump of hitting the ground after leaving it.
        /// </summary>
        private void ReportLanding()
        {
            if (m_Motor == null)
            {
                return;
            }

            bool grounded = m_Motor.IsGrounded;
            if (grounded && !m_WasGrounded)
            {
                GuardBrain.BroadcastNoise(transform.position, landingRadius);
            }

            m_WasGrounded = grounded;
        }

        /// <summary>
        /// Reports gunfire, wherever it came from.
        /// </summary>
        /// <remarks>
        /// Filtered to this player's own weapon: the event is static, so every PlayerNoise in the
        /// game hears every shot fired by anyone and would otherwise report it as its own.
        ///
        /// This covers shots that miss. A shot that hits a guard is reported separately by
        /// <see cref="GuardHealth"/>, which also turns that guard toward the shooter.
        /// </remarks>
        private void OnWeaponFired(GameObject shooter, Vector3 muzzlePosition)
        {
            if (!IsServer || shooter != gameObject)
            {
                return;
            }

            GuardBrain.BroadcastNoise(muzzlePosition, gunshotRadius);
        }

        #endregion
    }
}
