using UnityEngine;
using UnityEngine.Rendering;

namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// Draws a guard's field of view as a translucent cone that is visible while playing, clipped by
    /// walls and tinted by how close the guard is to spotting you.
    /// </summary>
    /// <remarks>
    /// <see cref="GuardVision"/> already draws a cone, but as a gizmo: Scene view only, and only for
    /// the selected guard. That is a debugging aid, not something a player can act on. Stealth is
    /// unfair without a readable answer to "can it see me from here", so this renders real geometry
    /// in the Game view.
    ///
    /// The cone is rebuilt every frame by raycasting the same <see cref="GuardVision.ObstacleMask"/>
    /// the detection uses. Drawing an unclipped wedge would be worse than drawing nothing: it would
    /// show the cone lying across a wall the guard cannot actually see through, and the player would
    /// learn to distrust it.
    ///
    /// It is drawn flat on the floor rather than at eye level, so it reads as an area on the ground
    /// the player can stay out of. The rays are still cast from the eye, so the shape describes what
    /// the guard can see: it reaches over a waist-high crate the guard looks across, and stops at a
    /// wall it cannot.
    ///
    /// The fan is flat, at the height of the guard's own feet. On stairs or a slope it will cut into
    /// the floor; every sample level here is flat enough that per-vertex ground sampling is not worth
    /// the extra raycast each.
    /// </remarks>
    [RequireComponent(typeof(GuardVision))]
    public class GuardVisionCone : MonoBehaviour
    {
        #region Fields & Properties

        [Header("Shape")]
        [Tooltip("Rays used to build the cone. More is smoother against complex geometry and costs a raycast each.")]
        [SerializeField, Range(8, 128)] private int segments = 48;

        [Tooltip("Lifts the cone off the floor so it does not z-fight with the ground. Raise it if the cone flickers.")]
        [SerializeField, Min(0f)] private float groundOffset = 0.05f;

        [Header("Colour")]
        [Tooltip("Colour while the guard has noticed nothing.")]
        [SerializeField] private Color calmColour = new Color(0.35f, 0.8f, 1f, 0.13f);

        [Tooltip("Colour once the guard is fully alerted and shooting.")]
        [SerializeField] private Color alertedColour = new Color(1f, 0.2f, 0.15f, 0.28f);

        private GuardVision m_Vision;
        private GuardBrain m_Brain;
        private Mesh m_Mesh;
        private MeshRenderer m_Renderer;
        private Material m_Material;
        private Vector3[] m_Vertices;
        private int[] m_Triangles;

        private static readonly int BaseColourId = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColourId = Shader.PropertyToID("_Color");

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            m_Vision = GetComponent<GuardVision>();
            m_Brain = GetComponent<GuardBrain>();
            Build();
        }

        private void OnDestroy()
        {
            // Meshes and materials created in code are not owned by any asset, so nothing else will
            // ever collect them.
            if (m_Mesh != null)
            {
                Destroy(m_Mesh);
            }

            if (m_Material != null)
            {
                Destroy(m_Material);
            }
        }

        private void LateUpdate()
        {
            // Late, so the cone matches where the guard ended the frame facing rather than trailing
            // a frame behind its own turn.
            if (m_Mesh == null || m_Vision == null)
            {
                return;
            }

            Reshape();
            Recolour();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Creates the child renderer that carries the cone.
        /// </summary>
        /// <remarks>
        /// A child rather than the guard itself: the guard's own object holds the collider the player
        /// shoots, and adding a renderer there would put cone geometry inside the hitbox.
        /// </remarks>
        private void Build()
        {
            var holder = new GameObject("Vision Cone");
            holder.transform.SetParent(transform, false);
            holder.layer = gameObject.layer;

            m_Mesh = new Mesh { name = "Guard Vision Cone" };
            m_Mesh.MarkDynamic();

            holder.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            m_Renderer = holder.AddComponent<MeshRenderer>();
            m_Renderer.shadowCastingMode = ShadowCastingMode.Off;
            m_Renderer.receiveShadows = false;
            m_Renderer.lightProbeUsage = LightProbeUsage.Off;
            m_Renderer.sharedMaterial = m_Material = CreateTransparentMaterial();

            m_Vertices = new Vector3[segments + 2];
            m_Triangles = new int[segments * 3];

            for (int i = 0; i < segments; i++)
            {
                m_Triangles[i * 3] = 0;
                m_Triangles[i * 3 + 1] = i + 1;
                m_Triangles[i * 3 + 2] = i + 2;
            }
        }

        /// <summary>
        /// Rebuilds the fan, stopping each ray where sight is actually blocked.
        /// </summary>
        private void Reshape()
        {
            // Local space, because the holder sits at the guard's own transform: the cone then turns
            // with the guard for free instead of being rebuilt in world space every frame.
            //
            // Two different heights on purpose. Rays are cast from the eye, because that is where
            // GuardVision casts from and the cone has to agree with what the guard can actually see.
            // The mesh is laid on the floor at the guard's feet, because a wedge floating at chest
            // height is read as an object in the world rather than as a marked-out area, and it hides
            // the ground the player is trying to judge.
            Vector3 floor = Vector3.up * groundOffset;
            Vector3 origin = m_Vision.EyePosition;
            float radius = m_Vision.ViewRadius;
            float half = m_Vision.ViewAngle * 0.5f;
            float step = m_Vision.ViewAngle / segments;

            m_Vertices[0] = floor;

            for (int i = 0; i <= segments; i++)
            {
                Vector3 direction = Quaternion.Euler(0f, -half + step * i, 0f) * Vector3.forward;
                Vector3 worldDirection = transform.TransformDirection(direction);

                float reach = radius;
                if (Physics.Raycast(origin, worldDirection, out RaycastHit hit, radius,
                        m_Vision.ObstacleMask, QueryTriggerInteraction.Ignore))
                {
                    reach = hit.distance;
                }

                m_Vertices[i + 1] = floor + direction * reach;
            }

            m_Mesh.Clear();
            m_Mesh.vertices = m_Vertices;
            m_Mesh.triangles = m_Triangles;
            m_Mesh.RecalculateBounds();
        }

        /// <summary>
        /// Tints the cone by how close the guard is to spotting the player.
        /// </summary>
        /// <remarks>
        /// Driven by awareness rather than only by state, so the cone warms up continuously as you
        /// are noticed. A colour that changes only on the final state change tells the player they
        /// have been caught at the moment it is already too late to do anything about it.
        /// </remarks>
        private void Recolour()
        {
            float heat = m_Brain == null
                ? 0f
                : (m_Brain.State == GuardAlertState.Alerted ? 1f : Mathf.Clamp01(m_Brain.Awareness));

            Color colour = Color.Lerp(calmColour, alertedColour, heat);

            if (m_Material.HasProperty(BaseColourId))
            {
                m_Material.SetColor(BaseColourId, colour);
            }
            else if (m_Material.HasProperty(LegacyColourId))
            {
                m_Material.SetColor(LegacyColourId, colour);
            }
        }

        /// <summary>
        /// Builds an unlit transparent material without needing one authored as an asset.
        /// </summary>
        /// <remarks>
        /// URP's Unlit shader defaults to opaque; transparency needs the surface keywords and blend
        /// modes set explicitly, or the cone renders as a solid wall of colour that hides the level.
        /// </remarks>
        private static Material CreateTransparentMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
                            ?? Shader.Find("Unlit/Transparent")
                            ?? Shader.Find("Sprites/Default");

            var material = new Material(shader) { name = "Guard Vision Cone" };

            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.renderQueue = (int)RenderQueue.Transparent;

            return material;
        }

        #endregion
    }
}
