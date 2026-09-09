using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blocks.Gameplay.Chase
{
    /// <summary>
    /// Arcade car handling: throttle and steering applied as forces to a Rigidbody, with grip faked
    /// by killing sideways velocity rather than simulated per wheel.
    /// </summary>
    /// <remarks>
    /// Deliberately not WheelColliders. Real wheel simulation needs suspension, per-wheel friction
    /// curves and a centre of mass that behaves, and it repays that tuning with flipping and
    /// juddering. A chase wants a car that goes where it is pointed, and that is four numbers.
    ///
    /// Sideways grip is the whole trick: a Rigidbody with only forward thrust slides like it is on
    /// ice, because nothing removes the lateral component of its velocity. Cancelling a fraction of
    /// that each step is what makes it feel like tyres.
    ///
    /// Server-authoritative like everything else here. The driver reads its own input and sends it
    /// up; the server does the physics and NetworkTransform carries the result back. The car is
    /// handed to the driver with ChangeOwnership so IsOwner identifies who should be reading input.
    /// </remarks>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(NetworkObject))]
    public class CarController : NetworkBehaviour
    {
        #region Fields & Properties

        [Header("Handling")]
        [Tooltip("Force applied when the throttle is down. Raise for quicker getaways.")]
        [SerializeField, Min(0f)] private float enginePower = 1800f;

        [Tooltip("Force applied when reversing or braking.")]
        [SerializeField, Min(0f)] private float brakePower = 1200f;

        [Tooltip("Top speed in metres per second. 25 is roughly 90 km/h.")]
        [SerializeField, Min(1f)] private float topSpeed = 25f;

        [Tooltip("Degrees per second of turn at full lock and full speed.")]
        [SerializeField, Min(0f)] private float steeringRate = 110f;

        [Tooltip("How much sideways slide is cancelled each step, 0 to 1. Low values feel like ice, high like rails.")]
        [SerializeField, Range(0f, 1f)] private float grip = 0.85f;

        [Tooltip("Slows the car when the throttle is released.")]
        [SerializeField, Min(0f)] private float rollingDrag = 0.6f;

        [Header("Stability")]
        [Tooltip("Lowers the centre of mass by this much, in metres. Without it the car tips over on the first hard turn.")]
        [SerializeField] private float centreOfMassDrop = 0.6f;

        private Rigidbody m_Body;
        private float m_Throttle;
        private float m_Steer;

        /// <summary>True once the sequence has handed the car over to a driver.</summary>
        public bool IsDrivable { get; private set; }

        /// <summary>Current speed in metres per second, for HUD or debugging.</summary>
        public float Speed => m_Body == null ? 0f : m_Body.linearVelocity.magnitude;

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            m_Body = GetComponent<Rigidbody>();

            // A box on wheels pivots around its centre. Dropping the centre of mass below the floor
            // of the body is the single change that stops it rolling onto its roof.
            m_Body.centerOfMass += Vector3.down * centreOfMassDrop;
        }

        private void Update()
        {
            if (!IsOwner || !IsDrivable)
            {
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            // Read directly rather than through the shared actions asset: those actions drive the
            // on-foot character, and rebinding them for the car would break walking.
            float throttle = (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed ? 1f : 0f)
                             - (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed ? 1f : 0f);
            float steer = (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed ? 1f : 0f)
                          - (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed ? 1f : 0f);

            if (IsServer)
            {
                m_Throttle = throttle;
                m_Steer = steer;
                return;
            }

            SubmitInputRpc(throttle, steer);
        }

        private void FixedUpdate()
        {
            if (!IsServer || !IsDrivable)
            {
                return;
            }

            Drive(Time.fixedDeltaTime);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Hands the car to a driver and starts accepting input.
        /// </summary>
        public void HandOverTo(ulong clientId)
        {
            if (!IsServer)
            {
                return;
            }

            if (NetworkObject.OwnerClientId != clientId)
            {
                NetworkObject.ChangeOwnership(clientId);
            }

            IsDrivable = true;
        }

        #endregion

        #region Private Methods

        [Rpc(SendTo.Server)]
        private void SubmitInputRpc(float throttle, float steer)
        {
            m_Throttle = Mathf.Clamp(throttle, -1f, 1f);
            m_Steer = Mathf.Clamp(steer, -1f, 1f);
        }

        private void Drive(float deltaTime)
        {
            Vector3 velocity = m_Body.linearVelocity;
            float forwardSpeed = Vector3.Dot(velocity, transform.forward);

            // Steering scales with speed, so the car cannot pirouette while stationary and does not
            // become twitchy at top speed.
            if (Mathf.Abs(forwardSpeed) > 0.5f)
            {
                float direction = Mathf.Sign(forwardSpeed);
                float authority = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / topSpeed);
                float yaw = m_Steer * steeringRate * authority * direction * deltaTime;
                m_Body.MoveRotation(m_Body.rotation * Quaternion.Euler(0f, yaw, 0f));
            }

            if (Mathf.Abs(m_Throttle) > 0.01f)
            {
                float power = m_Throttle > 0f ? enginePower : brakePower;
                if (Mathf.Abs(forwardSpeed) < topSpeed)
                {
                    m_Body.AddForce(transform.forward * (m_Throttle * power), ForceMode.Force);
                }
            }
            else
            {
                m_Body.AddForce(-velocity * rollingDrag, ForceMode.Force);
            }

            // Fake tyre grip: remove most of the sideways velocity so the car turns instead of
            // drifting outward. Doing this rather than simulating friction is what keeps handling
            // predictable.
            Vector3 sideways = transform.right * Vector3.Dot(velocity, transform.right);
            m_Body.AddForce(-sideways * grip, ForceMode.VelocityChange);
        }

        #endregion
    }
}
