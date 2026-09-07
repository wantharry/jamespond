using Blocks.Gameplay.Core;
using Unity.Netcode;
using UnityEngine;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Lets a guard shoot at whatever <see cref="GuardBrain"/> has locked on to. Hitscan, with a
    /// wind-up before the first shot and spread on every shot, so being spotted is a threat rather
    /// than an instant death.
    /// </summary>
    /// <remarks>
    /// Firing is server-only and routes damage through the same <see cref="IHittable"/> path the
    /// player's own weapons use, so armour, headshots and hit feedback all behave identically.
    ///
    /// Guards deliberately report <see cref="GuardAttackerId"/> rather than a real client id.
    /// <c>ShooterHitProcessor</c> discards any hit whose attacker matches the victim's own
    /// <c>OwnerClientId</c>, and the host is client 0 — so a guard firing as "client 0" would have
    /// every shot silently ignored by the host player.
    /// </remarks>
    [DisallowMultipleComponent]
    public class GuardWeapon : NetworkBehaviour
    {
        #region Constants

        /// <summary>
        /// Sentinel attacker id for shots fired by AI rather than a connected client. Chosen so it
        /// can never collide with a real client id.
        /// </summary>
        public const ulong GuardAttackerId = ulong.MaxValue;

        #endregion

        #region Fields & Properties

        [Header("Damage")]
        [Tooltip("Damage dealt per shot that connects.")]
        [SerializeField, Min(0f)] private float damage = 8f;

        [Tooltip("Maximum distance the guard will shoot from.")]
        [SerializeField, Min(1f)] private float range = 18f;

        [Header("Timing")]
        [Tooltip("Shots fired per second once the guard has settled its aim.")]
        [SerializeField, Min(0.1f)] private float shotsPerSecond = 1.5f;

        [Tooltip("Seconds the guard spends aiming after acquiring a target before its first shot. This is the player's window to break line of sight or take cover.")]
        [SerializeField, Min(0f)] private float aimTime = 0.7f;

        [Header("Accuracy")]
        [Tooltip("Cone of inaccuracy in degrees. 0 makes every shot a guaranteed hit.")]
        [SerializeField, Range(0f, 20f)] private float spreadDegrees = 4f;

        [Tooltip("Height the shot originates from, roughly the guard's shoulder.")]
        [SerializeField, Min(0f)] private float muzzleHeight = 1.5f;

        [Tooltip("Vertical offset on the target that the guard aims at, so it fires at the torso.")]
        [SerializeField, Min(0f)] private float targetCentreOffset = 1.0f;

        [Header("Layers")]
        [Tooltip("What the guard's bullets can hit. Must include the player and level geometry.")]
        [SerializeField] private LayerMask hitMask = ~0;

        [Header("Feedback")]
        [Tooltip("Optional sound played on every shot.")]
        [SerializeField] private SoundDef fireSound;

        [Tooltip("How long the tracer line stays visible, in seconds.")]
        [SerializeField, Min(0.01f)] private float tracerDuration = 0.05f;

        private float m_AimTimer;
        private float m_ShotCooldown;
        private bool m_HasAcquiredTarget;

        /// <summary>
        /// How far this guard is willing to shoot from. <see cref="GuardBrain"/> reads this to decide
        /// where to stop advancing.
        /// </summary>
        public float Range => range;

        #endregion

        #region Public Methods

        /// <summary>
        /// Attempts a shot at the given target. Safe to call every frame; internal timers decide
        /// whether a shot actually leaves the barrel. Server only.
        /// </summary>
        /// <param name="target">The transform to fire at.</param>
        /// <returns>True if a shot was fired this call.</returns>
        public bool TryFire(Transform target)
        {
            if (!IsServer || target == null)
            {
                return false;
            }

            // Wind-up. Resets whenever the guard loses and re-acquires a target, so repeatedly
            // breaking line of sight keeps buying the player time.
            if (!m_HasAcquiredTarget)
            {
                m_HasAcquiredTarget = true;
                m_AimTimer = aimTime;
            }

            if (m_AimTimer > 0f)
            {
                m_AimTimer -= Time.deltaTime;
                return false;
            }

            if (m_ShotCooldown > 0f)
            {
                m_ShotCooldown -= Time.deltaTime;
                return false;
            }

            m_ShotCooldown = 1f / shotsPerSecond;
            FireOnce(target);
            return true;
        }

        /// <summary>
        /// Clears aim and cooldown state. Called by the brain when the target is lost so the next
        /// engagement starts with a fresh wind-up.
        /// </summary>
        public void ResetAim()
        {
            m_HasAcquiredTarget = false;
            m_AimTimer = aimTime;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Fires a single hitscan shot with spread applied.
        /// </summary>
        private void FireOnce(Transform target)
        {
            Vector3 origin = transform.position + Vector3.up * muzzleHeight;
            Vector3 aimPoint = target.position + Vector3.up * targetCentreOffset;
            Vector3 direction = ApplySpread((aimPoint - origin).normalized);

            Vector3 endPoint = origin + direction * range;

            if (Physics.Raycast(origin, direction, out RaycastHit hit, range, hitMask, QueryTriggerInteraction.Ignore))
            {
                endPoint = hit.point;

                // Damage only lands on things that know how to receive it. Level geometry simply
                // stops the bullet.
                IHittable hittable = hit.collider.GetComponentInParent<IHittable>();
                if (hittable != null)
                {
                    hittable.OnHit(new HitInfo
                    {
                        amount = damage,
                        hitPoint = hit.point,
                        hitNormal = hit.normal,
                        attackerId = GuardAttackerId,
                        impactForce = direction * 20f
                    });
                }
            }

            ShowShotRpc(origin, endPoint);
        }

        /// <summary>
        /// Randomises the shot direction within a cone so guards miss sometimes.
        /// </summary>
        private Vector3 ApplySpread(Vector3 direction)
        {
            if (spreadDegrees <= 0f)
            {
                return direction;
            }

            Vector2 circle = Random.insideUnitCircle;
            Quaternion offset = Quaternion.Euler(circle.y * spreadDegrees, circle.x * spreadDegrees, 0f);
            return offset * direction;
        }

        /// <summary>
        /// Draws the shot on every peer. Without this the host would see tracers but connected
        /// clients would see guards firing invisibly.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        private void ShowShotRpc(Vector3 origin, Vector3 endPoint)
        {
            if (fireSound != null)
            {
                CoreDirector.RequestAudio(fireSound).WithPosition(origin).Play();
            }

            SpawnTracer(origin, endPoint);
        }

        /// <summary>
        /// Creates a short-lived line so the shot is actually visible. Built in code to avoid
        /// requiring a prefab reference on every guard.
        /// </summary>
        private void SpawnTracer(Vector3 origin, Vector3 endPoint)
        {
            GameObject tracer = new GameObject("GuardTracer");
            tracer.transform.position = origin;

            LineRenderer line = tracer.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, origin);
            line.SetPosition(1, endPoint);
            line.startWidth = 0.04f;
            line.endWidth = 0.01f;
            line.useWorldSpace = true;

            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader != null)
            {
                Material material = new Material(shader) { color = new Color(1f, 0.85f, 0.3f) };
                line.material = material;
            }

            Destroy(tracer, tracerDuration);
        }

        #endregion
    }
}
