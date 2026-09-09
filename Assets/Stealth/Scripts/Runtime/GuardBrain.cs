using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// The guard's state machine. Combines <see cref="GuardVision"/> (what it can see) with
    /// <see cref="GuardPatrol"/> (where it walks) and escalates through
    /// <see cref="GuardAlertState"/> as awareness builds.
    /// </summary>
    /// <remarks>
    /// All decision-making runs on the server only. Awareness and alert state are replicated to
    /// clients read-only so HUD elements can display a detection meter without being able to lie
    /// about it. Because a solo session is played as a host, this same code path serves both
    /// single-player and multiplayer with no branching.
    ///
    /// Required on the same GameObject: <see cref="NavMeshAgent"/>, <see cref="GuardVision"/>,
    /// <see cref="NetworkObject"/>. A <see cref="GuardPatrol"/> is optional — without one the guard
    /// holds position.
    /// </remarks>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(GuardVision))]
    [DisallowMultipleComponent]
    public class GuardBrain : NetworkBehaviour
    {
        #region Fields & Properties

        [Header("Detection")]
        [Tooltip("Awareness gained per second when the target is fully visible. 1 means a point-blank target is spotted in one second.")]
        [SerializeField, Min(0.01f)] private float detectionRate = 0.85f;

        [Tooltip("Awareness lost per second while nothing is visible.")]
        [SerializeField, Min(0.01f)] private float forgetRate = 0.35f;

        [Tooltip("Awareness above which the guard stops and looks. Below this it keeps patrolling.")]
        [SerializeField, Range(0.05f, 0.95f)] private float suspicionThreshold = 0.4f;

        [Header("Pursuit")]
        [Tooltip("Movement speed while patrolling.")]
        [SerializeField, Min(0f)] private float patrolSpeed = 1.8f;

        [Tooltip("Movement speed while chasing a spotted target.")]
        [SerializeField, Min(0f)] private float chaseSpeed = 4.2f;

        [Tooltip("How close an UNARMED guard gets to its target before holding position.")]
        [SerializeField, Min(0.5f)] private float pursuitStoppingDistance = 2f;

        [Tooltip("Preferred distance an ARMED guard holds while shooting. Capped by the weapon's own range.")]
        [SerializeField, Min(1f)] private float firingStandoff = 9f;

        [Tooltip("Add a GuardWeapon automatically if this guard has none. Turn off for a deliberately unarmed guard that only chases.")]
        [SerializeField] private bool armIfMissing = true;

        [Header("Combat movement")]
        [Tooltip("Keep moving while shooting instead of standing at the firing standoff. Off makes guards static turrets.")]
        [SerializeField] private bool strafeWhileAttacking = true;

        [Tooltip("Movement speed while circling a target under fire.")]
        [SerializeField, Min(0f)] private float strafeSpeed = 2.6f;

        [Tooltip("Seconds before the guard picks a new strafe position.")]
        [SerializeField, Min(0.2f)] private float strafeInterval = 1.8f;

        [Tooltip("How far around the target the guard moves each reposition, in degrees.")]
        [SerializeField, Range(10f, 170f)] private float strafeArcDegrees = 55f;

        [Tooltip("How far the guard may drift from its standoff before it closes or backs off instead of strafing.")]
        [SerializeField, Min(0.5f)] private float standoffTolerance = 2.5f;

        [Header("Searching")]
        [Tooltip("Seconds spent looking around the last known position before giving up and returning to patrol.")]
        [SerializeField, Min(0f)] private float searchDuration = 6f;

        [Tooltip("How fast the guard turns to face what it is looking at, in degrees per second.")]
        [SerializeField, Min(1f)] private float turnSpeed = 320f;

        [Header("Events (optional)")]
        [Tooltip("Raised on the server the moment this guard becomes fully alerted.")]
        [SerializeField] private Core.GameEvent onGuardAlerted;

        [Tooltip("Raised on the server when this guard gives up and returns to patrol.")]
        [SerializeField] private Core.GameEvent onGuardLostTarget;

        private NavMeshAgent m_Agent;
        private GuardVision m_Vision;
        private GuardPatrol m_Patrol;
        private GuardWeapon m_Weapon;

        private Transform m_Target;
        /// <summary>Every spawned guard, so a shot can be offered to each without a physics query.</summary>
        /// <remarks>
        /// A registry rather than Physics.OverlapSphere: hearing should not depend on colliders or on
        /// which layer a guard happens to sit on, and there are only ever a handful of guards.
        /// </remarks>
        private static readonly List<GuardBrain> s_Spawned = new List<GuardBrain>();

        private Vector3 m_LastKnownPosition;
        private float m_SearchTimer;
        private bool m_HasLastKnownPosition;

        private float m_StrafeTimer;
        private int m_StrafeDirection = 1;

        /// <summary>
        /// Replicated 0..1 detection meter. Server writes, everyone reads.
        /// </summary>
        [Tooltip("How far this guard hears gunfire, in metres. Hearing ignores walls, because sound goes round corners.")]
        [SerializeField, Min(0f)] private float hearingRadius = 18f;

        [Tooltip("Awareness a guard jumps to on hearing a shot nearby. Lower than being hit: it heard something, it was not shot.")]
        [SerializeField, Range(0f, 1f)] private float awarenessWhenHeard = 0.6f;

        [Tooltip("Awareness a guard jumps to when hit by a shot it survives. Below 1 it turns and looks; at 1 it goes straight to hunting you.")]
        [SerializeField, Range(0f, 1f)] private float awarenessWhenShot = 0.9f;

        private readonly NetworkVariable<float> m_Awareness = new NetworkVariable<float>(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        /// <summary>
        /// Replicated alert state. Server writes, everyone reads.
        /// </summary>
        private readonly NetworkVariable<GuardAlertState> m_State = new NetworkVariable<GuardAlertState>(
            GuardAlertState.Patrolling,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        /// <summary>
        /// Current detection meter in the range 0..1. Safe to read on any peer; drive a HUD from this.
        /// </summary>
        /// <summary>Guards currently spawned and alive.</summary>
        public static int AliveCount => s_Spawned.Count;

        /// <summary>
        /// True once at least one guard has ever spawned this session.
        /// </summary>
        /// <remarks>
        /// Without this, <see cref="AliveCount"/> of zero is ambiguous: it reads the same before the
        /// guards spawn as it does after the last one dies, so anything waiting for the level to be
        /// cleared would fire immediately on load.
        /// </remarks>
        public static bool AnyHaveSpawned { get; private set; }

        public float Awareness => m_Awareness.Value;

        /// <summary>
        /// The guard's current alert state. Safe to read on any peer.
        /// </summary>
        public GuardAlertState State => m_State.Value;

        /// <summary>
        /// Raised on every peer when the alert state changes, carrying the previous and new states.
        /// </summary>
        public event Action<GuardAlertState, GuardAlertState> OnStateChanged;

        #endregion

        #region Unity & Netcode Methods

        private void Awake()
        {
            m_Agent = GetComponent<NavMeshAgent>();
            m_Vision = GetComponent<GuardVision>();
            m_Patrol = GetComponent<GuardPatrol>();
            m_Weapon = GetComponent<GuardWeapon>();

            // Guards placed in a scene before GuardWeapon existed have no weapon, and adding a
            // component to the codebase does not attach it to objects already saved in a scene.
            // Rather than leave those guards silently walking up to the player and doing nothing,
            // arm them here. Its serialized defaults are playable, so this needs no configuration.
            if (m_Weapon == null && armIfMissing)
            {
                m_Weapon = gameObject.AddComponent<GuardWeapon>();
                Debug.Log($"[GuardBrain] '{name}' had no GuardWeapon; added one at runtime with default settings.", this);
            }
        }

        public override void OnNetworkSpawn()
        {
            m_State.OnValueChanged += HandleStateChanged;

            if (!s_Spawned.Contains(this))
            {
                s_Spawned.Add(this);
                AnyHaveSpawned = true;
            }

            // Only the server simulates the guard. Disabling the agent elsewhere stops clients
            // fighting the replicated transform with their own local pathfinding.
            if (!IsServer)
            {
                if (m_Agent != null)
                {
                    m_Agent.enabled = false;
                }
                return;
            }

            if (m_Agent != null)
            {
                m_Agent.speed = patrolSpeed;
                m_Agent.stoppingDistance = 0f;

                if (!m_Agent.isOnNavMesh)
                {
                    Debug.LogError(
                        $"[GuardBrain] '{name}' is not on a NavMesh. Bake one (Window > AI > Navigation) " +
                        "and make sure the guard is standing on walkable ground.", this);
                }
            }
        }

        public override void OnNetworkDespawn()
        {
            m_State.OnValueChanged -= HandleStateChanged;
            s_Spawned.Remove(this);
        }

        private void Update()
        {
            // Server-authoritative: clients render the replicated transform and nothing more.
            if (!IsServer)
            {
                return;
            }

            UpdateAwareness(out bool targetVisible);
            UpdateState(targetVisible);
            UpdateBehaviour(targetVisible);
        }

        /// <summary>
        /// Replicates one shot's visuals to every peer. Called on the server by
        /// <see cref="GuardWeapon"/>, which is a plain MonoBehaviour and so cannot send RPCs itself.
        /// </summary>
        /// <param name="origin">Muzzle position.</param>
        /// <param name="endPoint">Where the shot terminated.</param>
        /// <summary>
        /// Lets every guard in earshot know a noise was made somewhere.
        /// </summary>
        /// <remarks>
        /// Loudness is expressed as the radius the noise carries, in metres, rather than an abstract
        /// 0..1: "a gunshot carries 25 m, footsteps 8, a crouched step 3" is something you can reason
        /// about while tuning. A guard's own hearingRadius caps it, so a deaf guard stays deaf.
        ///
        /// Certainty falls off with distance. A noise at the very edge of earshot is a maybe; one
        /// made next to the guard is unmistakable and pushes past the suspicion threshold on its own,
        /// which is what makes standing close to a guard dangerous even without being seen.
        ///
        /// Distance only, deliberately: hearing is not line of sight, and requiring one would mean a
        /// guard on the far side of a doorway ignores a rifle going off next to it.
        /// </remarks>
        /// <param name="worldPosition">Where the noise was made.</param>
        /// <param name="radius">How far it carries, in metres.</param>
        /// <param name="ignore">A guard that is reacting some other way and should not be told twice.</param>
        public static void BroadcastNoise(Vector3 worldPosition, float radius, GuardBrain ignore = null)
        {
            foreach (GuardBrain brain in s_Spawned)
            {
                if (brain == null || brain == ignore || !brain.IsServer)
                {
                    continue;
                }

                float reach = Mathf.Min(radius, brain.hearingRadius);
                if (reach <= 0f)
                {
                    continue;
                }

                float distance = Vector3.Distance(brain.transform.position, worldPosition);
                if (distance <= reach)
                {
                    brain.ReportHeardNoise(worldPosition, 1f - distance / reach);
                }
            }
        }

        /// <summary>
        /// Tells the guard it heard something, so it looks that way.
        /// </summary>
        /// <remarks>
        /// Weaker than <see cref="ReportAttackedFrom"/> on purpose: a guard that was hit knows
        /// exactly what happened, one that merely heard something is only curious.
        /// </remarks>
        /// <param name="worldPosition">Where the noise came from.</param>
        /// <param name="closeness">0 at the edge of earshot, 1 at the source.</param>
        public void ReportHeardNoise(Vector3 worldPosition, float closeness)
        {
            if (!IsServer)
            {
                return;
            }

            m_LastKnownPosition = worldPosition;
            m_HasLastKnownPosition = true;
            m_Awareness.Value = Mathf.Max(m_Awareness.Value, awarenessWhenHeard * Mathf.Clamp01(closeness));
        }

        /// <summary>
        /// Tells the guard it was shot at from somewhere, so it stops and looks that way.
        /// </summary>
        /// <remarks>
        /// Being hit is information a guard plainly has, and ignoring it is the single most obvious
        /// way for the AI to look broken: you shoot someone in the back and they keep strolling.
        ///
        /// This reuses the ordinary awareness path rather than forcing a state. Feeding the position
        /// in as the last known position means the existing states do the work — Suspicious stops and
        /// turns toward it, Investigating walks to it — and if the guard then actually sees you,
        /// escalation to Alerted happens through the same rules as being spotted any other way.
        ///
        /// Server only: awareness and the alert state are server-authored, and the resulting turn
        /// reaches clients through NetworkTransform.
        /// </remarks>
        /// <param name="worldPosition">Where the shot came from.</param>
        public void ReportAttackedFrom(Vector3 worldPosition)
        {
            if (!IsServer)
            {
                return;
            }

            m_LastKnownPosition = worldPosition;
            m_HasLastKnownPosition = true;
            m_Awareness.Value = Mathf.Max(m_Awareness.Value, awarenessWhenShot);
        }

        public void ReportShot(Vector3 origin, Vector3 endPoint)
        {
            ShowShotRpc(origin, endPoint);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Draws the shot on every peer, including the server that fired it.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        private void ShowShotRpc(Vector3 origin, Vector3 endPoint)
        {
            if (m_Weapon != null)
            {
                m_Weapon.RenderShot(origin, endPoint);
            }
        }

        /// <summary>
        /// Scans for a target and moves the awareness meter toward or away from full.
        /// </summary>
        /// <param name="targetVisible">True when a player is in view this frame.</param>
        private void UpdateAwareness(out bool targetVisible)
        {
            Transform seen = null;
            float strength = 0f;
            targetVisible = m_Vision != null && m_Vision.Scan(out seen, out strength);

            if (targetVisible)
            {
                m_Target = seen;

                // Remember where the target was, so losing sight gives something to investigate.
                if (m_Target != null)
                {
                    m_LastKnownPosition = m_Target.position;
                    m_HasLastKnownPosition = true;
                }

                // Distance-scaled: a target at the edge of the cone fills the meter slowly.
                m_Awareness.Value = Mathf.Clamp01(m_Awareness.Value + detectionRate * strength * Time.deltaTime);
            }
            else
            {
                m_Target = null;
                m_Awareness.Value = Mathf.Clamp01(m_Awareness.Value - forgetRate * Time.deltaTime);
            }
        }

        /// <summary>
        /// Maps the awareness meter and target visibility onto an alert state.
        /// </summary>
        private void UpdateState(bool targetVisible)
        {
            GuardAlertState previous = m_State.Value;
            GuardAlertState next = previous;

            if (m_Awareness.Value >= 1f && targetVisible)
            {
                next = GuardAlertState.Alerted;
            }
            else if (previous == GuardAlertState.Alerted && !targetVisible)
            {
                // Lost sight of a confirmed target: go and look where it was.
                next = GuardAlertState.Investigating;
                m_SearchTimer = searchDuration;
            }
            else if (previous == GuardAlertState.Investigating)
            {
                // Spotting the target again re-escalates; otherwise search until the timer runs out.
                if (targetVisible && m_Awareness.Value >= 1f)
                {
                    next = GuardAlertState.Alerted;
                }
                else if (m_SearchTimer <= 0f)
                {
                    next = GuardAlertState.Patrolling;
                }
            }
            else if (m_Awareness.Value >= suspicionThreshold)
            {
                next = GuardAlertState.Suspicious;
            }
            else
            {
                next = GuardAlertState.Patrolling;
            }

            if (next == previous)
            {
                return;
            }

            m_State.Value = next;

            // Leaving combat clears the wind-up, so re-acquiring the player costs the guard its
            // aim time again rather than letting it resume firing instantly.
            if (previous == GuardAlertState.Alerted)
            {
                m_Weapon?.ResetAim();
            }

            if (next == GuardAlertState.Alerted)
            {
                onGuardAlerted?.Raise();
            }
            else if (next == GuardAlertState.Patrolling)
            {
                m_HasLastKnownPosition = false;
                onGuardLostTarget?.Raise();
                m_Patrol?.ResumeNearest(transform.position);
            }
        }

        /// <summary>
        /// Drives movement for the current state.
        /// </summary>
        private void UpdateBehaviour(bool targetVisible)
        {
            if (m_Agent == null || !m_Agent.isOnNavMesh)
            {
                return;
            }

            // Only combat takes manual control of facing; every other state lets the agent turn to
            // face its own path. Restored here so leaving combat cannot strand a guard unable to
            // turn while patrolling.
            if (m_State.Value != GuardAlertState.Alerted)
            {
                m_Agent.updateRotation = true;
            }

            switch (m_State.Value)
            {
                case GuardAlertState.Patrolling:
                    m_Agent.speed = patrolSpeed;
                    m_Agent.stoppingDistance = 0f;
                    m_Agent.isStopped = false;
                    m_Patrol?.Tick(m_Agent);
                    break;

                case GuardAlertState.Suspicious:
                    // Hold position and turn toward whatever caught the guard's attention. Standing
                    // still is what gives the player a window to break line of sight.
                    m_Agent.isStopped = true;
                    if (m_HasLastKnownPosition)
                    {
                        FaceTowards(m_LastKnownPosition);
                    }
                    break;

                case GuardAlertState.Investigating:
                    m_Agent.isStopped = false;
                    m_Agent.speed = chaseSpeed;
                    m_Agent.stoppingDistance = 0f;
                    m_SearchTimer -= Time.deltaTime;

                    if (m_HasLastKnownPosition)
                    {
                        m_Agent.SetDestination(m_LastKnownPosition);

                        // Arrived at the last known position with nothing there: look around while
                        // the search timer drains.
                        if (!m_Agent.pathPending && m_Agent.remainingDistance <= 1f)
                        {
                            transform.Rotate(Vector3.up, turnSpeed * 0.35f * Time.deltaTime, Space.World);
                        }
                    }
                    break;

                case GuardAlertState.Alerted:
                    m_Agent.speed = chaseSpeed;

                    if (targetVisible && m_Target != null)
                    {
                        float distanceToTarget = Vector3.Distance(transform.position, m_Target.position);
                        float holdDistance = m_Weapon != null ? firingStandoff : pursuitStoppingDistance;

                        // The agent must not steer the guard's facing during combat, or it would
                        // turn to look where it is walking and fire sideways while strafing.
                        m_Agent.updateRotation = false;
                        m_Agent.isStopped = false;
                        m_Agent.stoppingDistance = 0f;

                        UpdateCombatMovement(distanceToTarget, holdDistance);
                        FaceTowards(m_Target.position);

                        // Line of sight is already established: targetVisible comes from
                        // GuardVision, which has done the cone and occlusion checks this frame.
                        if (m_Weapon != null && distanceToTarget <= m_Weapon.Range)
                        {
                            m_Weapon.TryFire(m_Target);
                        }
                    }
                    else
                    {
                        m_Agent.updateRotation = true;
                        m_Agent.isStopped = false;
                        m_Agent.stoppingDistance = pursuitStoppingDistance;
                    }
                    break;
            }
        }

        /// <summary>
        /// Moves the guard while it is engaging: close the gap when too far, back off when too
        /// close, and circle the target when comfortably at its standoff.
        /// </summary>
        /// <remarks>
        /// Strafing exists so guards are not static turrets, and because hit chance depends on
        /// whether a target is moving — a guard that stands still while shooting is asking to be
        /// shot back at full accuracy once it can be damaged.
        /// </remarks>
        /// <param name="distanceToTarget">Current distance to the target.</param>
        /// <param name="holdDistance">The standoff the guard wants to keep.</param>
        private void UpdateCombatMovement(float distanceToTarget, float holdDistance)
        {
            if (m_Target == null)
            {
                return;
            }

            // Too far to shoot comfortably: close in at full speed.
            if (distanceToTarget > holdDistance + standoffTolerance)
            {
                m_Agent.speed = chaseSpeed;
                m_Agent.SetDestination(m_Target.position);
                return;
            }

            // Crowded: give ground rather than walking into the player.
            if (distanceToTarget < holdDistance - standoffTolerance)
            {
                m_Agent.speed = chaseSpeed;
                Vector3 away = (transform.position - m_Target.position).normalized;
                MoveToNavigable(m_Target.position + away * holdDistance);
                return;
            }

            if (!strafeWhileAttacking)
            {
                m_Agent.isStopped = true;
                return;
            }

            // In the comfortable band: circle. Reversing direction periodically keeps the movement
            // from reading as a predictable orbit.
            m_Agent.speed = strafeSpeed;
            m_StrafeTimer -= Time.deltaTime;

            bool arrived = !m_Agent.pathPending && m_Agent.remainingDistance <= 0.5f;
            if (m_StrafeTimer <= 0f || arrived)
            {
                m_StrafeTimer = strafeInterval;

                // Mostly continue the same way round, occasionally switch, so the player cannot
                // simply lead the guard in one direction.
                if (UnityEngine.Random.value < 0.35f)
                {
                    m_StrafeDirection = -m_StrafeDirection;
                }

                Vector3 bearing = (transform.position - m_Target.position).normalized;
                Vector3 rotated = Quaternion.AngleAxis(strafeArcDegrees * m_StrafeDirection, Vector3.up) * bearing;
                MoveToNavigable(m_Target.position + rotated * holdDistance);
            }
        }

        /// <summary>
        /// Sends the agent to the nearest navigable point to a desired position. Strafe targets are
        /// computed geometrically and can land inside walls or off the mesh.
        /// </summary>
        private void MoveToNavigable(Vector3 desired)
        {
            if (NavMesh.SamplePosition(desired, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            {
                m_Agent.SetDestination(hit.position);
            }
            else
            {
                // Nowhere sensible to circle to; hold and keep shooting rather than stalling.
                m_StrafeDirection = -m_StrafeDirection;
            }
        }

        /// <summary>
        /// Rotates the guard smoothly toward a world position, yaw only so it never tips over.
        /// </summary>
        private void FaceTowards(Vector3 worldPosition)
        {
            Vector3 flat = worldPosition - transform.position;
            flat.y = 0f;

            if (flat.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            Quaternion desired = Quaternion.LookRotation(flat);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, desired, turnSpeed * Time.deltaTime);
        }

        /// <summary>
        /// Re-raises the replicated state change as a local C# event for HUD and audio hooks.
        /// </summary>
        private void HandleStateChanged(GuardAlertState previous, GuardAlertState current)
        {
            OnStateChanged?.Invoke(previous, current);
        }

        #endregion

        #region Gizmos

        /// <summary>
        /// Colours the guard by alert state and marks the position it is investigating, so the state
        /// machine can be read at a glance while play testing.
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            Gizmos.color = m_State.Value switch
            {
                GuardAlertState.Alerted => Color.red,
                GuardAlertState.Investigating => new Color(1f, 0.45f, 0f),
                GuardAlertState.Suspicious => Color.yellow,
                _ => Color.green
            };

            Gizmos.DrawWireSphere(transform.position + Vector3.up * 2.2f, 0.25f);

            if (m_HasLastKnownPosition && m_State.Value >= GuardAlertState.Investigating)
            {
                Gizmos.DrawWireCube(m_LastKnownPosition, Vector3.one * 0.5f);
                Gizmos.DrawLine(transform.position, m_LastKnownPosition);
            }
        }

        #endregion
    }
}
