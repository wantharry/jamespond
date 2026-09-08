using System.Collections;
using Blocks.Gameplay.Core;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Makes a guard killable. Routes incoming damage through the same <see cref="IHittable"/> path
    /// the player's own body uses, so the player's weapons, impact effects and hit feedback all work
    /// on guards with no special cases.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT reproduce ShooterHitProcessor's "ignore hits from my own OwnerClientId"
    /// guard. A scene-placed guard is owned by the server, and the host is client 0 — so that check
    /// would make every guard immune to the host player, which is the only player in single player.
    /// It is the mirror image of the bug that makes <see cref="GuardWeapon"/> fire as
    /// <see cref="GuardWeapon.GuardAttackerId"/> rather than as client 0.
    ///
    /// Self-inflicted hits are not a concern here: HitscanShooting already discards any hit whose
    /// collider shares a transform root with the shooter.
    /// </remarks>
    [DisallowMultipleComponent]
    public class GuardHealth : HitProcessor
    {
        #region Fields & Properties

        [Header("Health")]
        [Tooltip("Hit points a guard starts with. The player's assault rifle does roughly 10 per shot, so 40 is about four hits.")]
        [SerializeField, Min(1f)] private float maxHealth = 40f;

        [Header("Death")]
        [Tooltip("Seconds the body stays before it is removed, so the kill is visible rather than the guard blinking out.")]
        [SerializeField, Min(0f)] private float despawnDelay = 1.5f;

        private readonly NetworkVariable<float> m_Health = new NetworkVariable<float>(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        /// <summary>Current hit points. Meaningful on every peer; only the server writes it.</summary>
        public float Health => m_Health.Value;

        /// <summary>Fraction of health remaining, for any UI that wants a bar.</summary>
        public float HealthFraction => maxHealth <= 0f ? 0f : Mathf.Clamp01(m_Health.Value / maxHealth);

        /// <summary>True once the guard has been killed. Checked before applying further damage.</summary>
        public bool IsDead => m_Health.Value <= 0f;

        #endregion

        #region Network Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (IsServer)
            {
                m_Health.Value = maxHealth;
            }

            // Every peer watches the replicated value and plays the fall itself. Sending a death
            // RPC as well would double up on the host, which both writes the value and receives it.
            m_Health.OnValueChanged += OnHealthChanged;
        }

        public override void OnNetworkDespawn()
        {
            m_Health.OnValueChanged -= OnHealthChanged;
            base.OnNetworkDespawn();
        }

        /// <summary>
        /// Plays the death fall on this peer once health reaches zero.
        /// </summary>
        private void OnHealthChanged(float previous, float current)
        {
            if (current > 0f)
            {
                return;
            }

            if (TryGetComponent(out GuardDeathAnimation death))
            {
                death.Play();
            }
        }

        #endregion

        #region Hit Handling

        /// <summary>
        /// Applies validated damage. Runs on the server only; the base class routes client hits here.
        /// </summary>
        protected override void HandleHit(HitInfo info)
        {
            if (!IsServer || IsDead)
            {
                return;
            }

            m_Health.Value = Mathf.Max(0f, m_Health.Value - info.amount);

            if (m_Health.Value <= 0f)
            {
                Die();
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Shuts the guard down and schedules removal of the body.
        /// </summary>
        /// <remarks>
        /// Components are disabled rather than destroyed so the corpse keeps its pose for the delay.
        /// The collider goes immediately, otherwise a dead guard still blocks the player's shots and
        /// soaks bullets meant for whoever is behind it.
        /// </remarks>
        private void Die()
        {
            foreach (Collider collider in GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
            }

            if (TryGetComponent(out NavMeshAgent agent))
            {
                agent.isStopped = true;
                agent.enabled = false;
            }

            if (TryGetComponent(out GuardBrain brain))
            {
                brain.enabled = false;
            }

            if (TryGetComponent(out GuardWeapon weapon))
            {
                weapon.enabled = false;
            }

            if (TryGetComponent(out GuardAnimatorDriver animatorDriver))
            {
                animatorDriver.enabled = false;
            }

            if (TryGetComponent(out GuardDeathAnimation death))
            {
                death.Play();
            }

            StartCoroutine(DespawnAfterDelay());
        }

        private IEnumerator DespawnAfterDelay()
        {
            yield return new WaitForSeconds(despawnDelay);

            if (NetworkObject != null && NetworkObject.IsSpawned)
            {
                NetworkObject.Despawn();
            }
        }

        #endregion
    }
}
