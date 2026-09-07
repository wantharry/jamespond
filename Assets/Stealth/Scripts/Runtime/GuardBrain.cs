using System;
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
        private Vector3 m_LastKnownPosition;
        private float m_SearchTimer;
        private bool m_HasLastKnownPosition;

        /// <summary>
        /// Replicated 0..1 detection meter. Server writes, everyone reads.
        /// </summary>
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
        }

        public override void OnNetworkSpawn()
        {
            m_State.OnValueChanged += HandleStateChanged;

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

        #endregion

        #region Private Methods

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
                        // An armed guard holds its ground at weapon range and shoots. An unarmed one
                        // closes to melee distance, which is the old behaviour.
                        float engageDistance = m_Weapon != null
                            ? Mathf.Min(m_Weapon.Range, firingStandoff)
                            : pursuitStoppingDistance;

                        m_Agent.stoppingDistance = engageDistance;

                        float distanceToTarget = Vector3.Distance(transform.position, m_Target.position);
                        bool inFiringPosition = m_Weapon != null && distanceToTarget <= engageDistance;

                        // Stop moving before firing so shots are not sprayed while running.
                        m_Agent.isStopped = inFiringPosition;

                        if (!inFiringPosition)
                        {
                            m_Agent.SetDestination(m_Target.position);
                        }

                        FaceTowards(m_Target.position);

                        if (inFiringPosition)
                        {
                            m_Weapon.TryFire(m_Target);
                        }
                    }
                    else
                    {
                        m_Agent.isStopped = false;
                        m_Agent.stoppingDistance = pursuitStoppingDistance;
                    }
                    break;
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
