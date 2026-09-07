using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Walks a guard around a fixed set of waypoints, pausing at each one. Movement only; it holds
    /// no opinion about whether the guard has seen anything. <see cref="GuardBrain"/> calls
    /// <see cref="Tick"/> while patrolling and simply stops calling it once the guard is distracted.
    /// </summary>
    /// <remarks>
    /// Waypoints are plain <see cref="Transform"/>s placed in the scene, so a level designer can
    /// drag empties around without touching code. With fewer than two waypoints the guard just
    /// stands at its spawn position and turns in place.
    /// </remarks>
    [DisallowMultipleComponent]
    public class GuardPatrol : MonoBehaviour
    {
        #region Fields & Properties

        [Header("Route")]
        [Tooltip("Waypoints to walk, in order. Leave empty for a stationary guard.")]
        [SerializeField] private List<Transform> waypoints = new List<Transform>();

        [Tooltip("Walk the route forwards then backwards, instead of looping from the last waypoint straight back to the first.")]
        [SerializeField] private bool pingPong;

        [Header("Timing")]
        [Tooltip("Seconds to wait at each waypoint before moving on.")]
        [SerializeField, Min(0f)] private float waitAtWaypoint = 2f;

        [Tooltip("How close the guard must get before a waypoint counts as reached.")]
        [SerializeField, Min(0.1f)] private float arriveDistance = 0.6f;

        private int m_CurrentIndex;
        private int m_Direction = 1;
        private float m_WaitTimer;

        /// <summary>
        /// True when a route is actually configured. A guard without waypoints is stationary.
        /// </summary>
        public bool HasRoute => waypoints != null && waypoints.Count > 0;

        #endregion

        #region Public Methods

        /// <summary>
        /// Advances the patrol. Call once per server tick while the guard is unaware.
        /// </summary>
        /// <param name="agent">The guard's navmesh agent.</param>
        public void Tick(NavMeshAgent agent)
        {
            if (agent == null || !agent.isOnNavMesh || !HasRoute)
            {
                return;
            }

            Transform destination = CurrentWaypoint();
            if (destination == null)
            {
                // A waypoint was deleted at runtime; skip past it rather than stalling.
                Advance();
                return;
            }

            // Pause on arrival, then move to the next point.
            if (!agent.pathPending && agent.remainingDistance <= arriveDistance)
            {
                m_WaitTimer += Time.deltaTime;
                if (m_WaitTimer >= waitAtWaypoint)
                {
                    m_WaitTimer = 0f;
                    Advance();
                    destination = CurrentWaypoint();
                }
                else
                {
                    return;
                }
            }

            if (destination != null)
            {
                agent.SetDestination(destination.position);
            }
        }

        /// <summary>
        /// Sends the guard back to its route after a distraction ends. Picks the nearest waypoint so
        /// it does not walk all the way back to where it was interrupted.
        /// </summary>
        /// <param name="fromPosition">Where the guard is resuming from.</param>
        public void ResumeNearest(Vector3 fromPosition)
        {
            m_WaitTimer = 0f;

            if (!HasRoute)
            {
                return;
            }

            float best = float.MaxValue;
            for (int i = 0; i < waypoints.Count; i++)
            {
                if (waypoints[i] == null)
                {
                    continue;
                }

                float sqr = (waypoints[i].position - fromPosition).sqrMagnitude;
                if (sqr < best)
                {
                    best = sqr;
                    m_CurrentIndex = i;
                }
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// The waypoint the guard is currently heading for, or null if the route is empty.
        /// </summary>
        private Transform CurrentWaypoint()
        {
            if (!HasRoute)
            {
                return null;
            }

            m_CurrentIndex = Mathf.Clamp(m_CurrentIndex, 0, waypoints.Count - 1);
            return waypoints[m_CurrentIndex];
        }

        /// <summary>
        /// Steps to the next waypoint, either looping or reversing at the ends of the route.
        /// </summary>
        private void Advance()
        {
            if (!HasRoute)
            {
                return;
            }

            if (waypoints.Count == 1)
            {
                m_CurrentIndex = 0;
                return;
            }

            if (pingPong)
            {
                m_CurrentIndex += m_Direction;

                // Bounce off either end of the route.
                if (m_CurrentIndex >= waypoints.Count)
                {
                    m_CurrentIndex = waypoints.Count - 2;
                    m_Direction = -1;
                }
                else if (m_CurrentIndex < 0)
                {
                    m_CurrentIndex = 1;
                    m_Direction = 1;
                }
            }
            else
            {
                m_CurrentIndex = (m_CurrentIndex + 1) % waypoints.Count;
            }
        }

        #endregion

        #region Gizmos

        /// <summary>
        /// Draws the patrol route so it can be laid out visually in the Scene view.
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (!HasRoute)
            {
                return;
            }

            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);

            for (int i = 0; i < waypoints.Count; i++)
            {
                if (waypoints[i] == null)
                {
                    continue;
                }

                Gizmos.DrawWireSphere(waypoints[i].position, 0.4f);

                // Connect to the next point, closing the loop only when not ping-ponging.
                Transform next = null;
                if (i + 1 < waypoints.Count)
                {
                    next = waypoints[i + 1];
                }
                else if (!pingPong)
                {
                    next = waypoints[0];
                }

                if (next != null)
                {
                    Gizmos.DrawLine(waypoints[i].position, next.position);
                }
            }
        }

        #endregion
    }
}
