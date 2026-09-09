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

        /// <summary>How far back along a bullet's path to place an unidentified shooter.</summary>
        private const float TracebackDistance = 10f;

        /// <summary>How far a gunshot carries, in metres. The loudest thing in the game.</summary>
        private const float GunshotRadius = 25f;

        private bool m_Died;

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

            // Despawn(false) leaves the in-scene object in place, so the body has to be hidden
            // explicitly. Guarded on death so a despawn from a shutdown or scene unload does not
            // blank out guards that are still alive.
            if (m_Died)
            {
                gameObject.SetActive(false);
            }

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

            m_Died = true;

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
                return;
            }

            // Survived it: turn and look at whoever fired, and let the others hear it. Broadcasting
            // even when this guard has no brain matters — the shot was still audible.
            if (TryLocateAttacker(info, out Vector3 from))
            {
                GuardBrain brain = GetComponent<GuardBrain>();
                if (brain != null)
                {
                    brain.ReportAttackedFrom(from);
                }

                GuardBrain.BroadcastNoise(from, GunshotRadius, brain);
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
        /// <summary>
        /// Works out where a shot came from.
        /// </summary>
        /// <remarks>
        /// The attacker's own position is preferred, because that is what the guard should end up
        /// facing. It is not always available: guards fire as
        /// <see cref="GuardWeapon.GuardAttackerId"/> rather than a real client id, and a client can
        /// disconnect between firing and the hit being processed.
        ///
        /// The fallback walks back up the bullet's own travel direction from the impact point, which
        /// gives the right facing even when the shooter cannot be identified. The distance is
        /// arbitrary: only the direction from the guard to that point is used.
        /// </remarks>
        private bool TryLocateAttacker(HitInfo info, out Vector3 position)
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null &&
                manager.ConnectedClients.TryGetValue(info.attackerId, out NetworkClient client) &&
                client.PlayerObject != null)
            {
                position = client.PlayerObject.transform.position;
                return true;
            }

            if (info.impactForce.sqrMagnitude > Mathf.Epsilon)
            {
                position = info.hitPoint - info.impactForce.normalized * TracebackDistance;
                return true;
            }

            position = default;
            return false;
        }

        private void Die()
        {
            m_Died = true;

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
                // Despawn(false) rather than the default Despawn(true): destroying an in-scene
                // NetworkObject is unsupported. Netcode warns about it and the scene's object
                // registry still expects the object to exist, which misbehaves on a scene reload.
                // OnNetworkDespawn hides the body on every peer instead.
                NetworkObject.Despawn(false);
            }
        }

        #endregion
    }
}
