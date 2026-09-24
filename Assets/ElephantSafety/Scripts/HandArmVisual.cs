using UnityEngine;
using UnityEngine.XR.Hands;

namespace ElephantSafety
{
    /// <summary>
    /// Shows a human hand and arm for one tracked hand: the skinned hand mesh from the XR Hands
    /// "HandVisualizer" sample, plus a generated arm that tapers from an estimated shoulder,
    /// through the elbow, to the tracked wrist.
    /// Place this on a GameObject under the XR Origin's Camera Offset, next to an <see cref="XRHandTrackingEvents"/>.
    /// </summary>
    [RequireComponent(typeof(XRHandTrackingEvents))]
    public class HandArmVisual : MonoBehaviour
    {
        [SerializeField, Tooltip("Head / main camera, used to estimate where the shoulder is.")]
        Transform m_Head;

        [SerializeField]
        Material m_SkinMaterial;

        [Header("Arm proportions (metres)")]
        [SerializeField] float m_UpperArmLength = 0.29f;
        [SerializeField] float m_ForearmLength = 0.27f;
        [SerializeField] float m_ShoulderWidth = 0.19f;
        [SerializeField] float m_ShoulderDrop = 0.24f;

        [Header("Arm thickness (radius in metres, shoulder to wrist)")]
        [SerializeField] float m_ShoulderRadius = 0.055f;
        [SerializeField] float m_ElbowRadius = 0.043f;
        [SerializeField] float m_WristRadius = 0.028f;

        const int k_Sides = 14;      // ring resolution around the arm
        const int k_Samples = 14;    // rings along the arm

        XRHandTrackingEvents m_TrackingEvents;
        Transform m_VisualRoot;
        GameObject m_HandInstance;
        Mesh m_ArmMesh;
        Vector3[] m_ArmVertices;
        Vector3[] m_ArmNormals;

        public Transform head { get => m_Head; set => m_Head = value; }
        public Material skinMaterial { get => m_SkinMaterial; set => m_SkinMaterial = value; }

        void Awake()
        {
            m_TrackingEvents = GetComponent<XRHandTrackingEvents>();
            if (m_Head == null && Camera.main != null)
                m_Head = Camera.main.transform;

            m_VisualRoot = new GameObject("Visual").transform;
            m_VisualRoot.SetParent(transform, false);

            SpawnHandMesh();
            BuildArm();
            SetVisible(false);
        }

        void OnEnable()
        {
            m_TrackingEvents.jointsUpdated.AddListener(OnJointsUpdated);
            m_TrackingEvents.trackingChanged.AddListener(SetVisible);
        }

        void OnDisable()
        {
            m_TrackingEvents.jointsUpdated.RemoveListener(OnJointsUpdated);
            m_TrackingEvents.trackingChanged.RemoveListener(SetVisible);
        }

        void OnDestroy()
        {
            if (m_ArmMesh != null)
                Destroy(m_ArmMesh);
        }

        /// <summary>The sample's hand prefab drives itself from the subsystem, so it just needs parenting.</summary>
        void SpawnHandMesh()
        {
            var prefabs = HandVisualPrefabs.Load();
            var prefab = prefabs == null
                ? null
                : m_TrackingEvents.handedness == Handedness.Left ? prefabs.leftHandPrefab : prefabs.rightHandPrefab;

            if (prefab == null)
            {
                Debug.LogWarning($"No hand mesh prefab for {m_TrackingEvents.handedness}. " +
                                 "Run Elephant Safety > Upgrade Hands And Clap to import them.", this);
                return;
            }

            m_HandInstance = Instantiate(prefab, m_VisualRoot);
            m_HandInstance.name = $"{m_TrackingEvents.handedness} Hand Mesh";
            m_HandInstance.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

            // The sample hands ship with their own material; use the arm's skin so the two match.
            if (m_SkinMaterial != null)
            {
                foreach (var renderer in m_HandInstance.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.sharedMaterial = m_SkinMaterial;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }
        }

        void BuildArm()
        {
            var go = new GameObject("Arm");
            go.transform.SetParent(m_VisualRoot, false);

            m_ArmMesh = new Mesh { name = "Arm", indexFormat = UnityEngine.Rendering.IndexFormat.UInt16 };
            m_ArmMesh.MarkDynamic();

            var vertexCount = k_Samples * k_Sides + 2; // + centre points capping each end
            m_ArmVertices = new Vector3[vertexCount];
            m_ArmNormals = new Vector3[vertexCount];

            var triangles = new int[(k_Samples - 1) * k_Sides * 6 + k_Sides * 6];
            var t = 0;
            for (var ring = 0; ring < k_Samples - 1; ring++)
            {
                for (var side = 0; side < k_Sides; side++)
                {
                    var next = (side + 1) % k_Sides;
                    var a = ring * k_Sides + side;
                    var b = ring * k_Sides + next;
                    var c = (ring + 1) * k_Sides + side;
                    var d = (ring + 1) * k_Sides + next;
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                }
            }

            var shoulderCap = k_Samples * k_Sides;
            var wristCap = shoulderCap + 1;
            for (var side = 0; side < k_Sides; side++)
            {
                var next = (side + 1) % k_Sides;
                triangles[t++] = shoulderCap; triangles[t++] = side; triangles[t++] = next;
                var last = (k_Samples - 1) * k_Sides;
                triangles[t++] = wristCap; triangles[t++] = last + next; triangles[t++] = last + side;
            }

            m_ArmMesh.vertices = m_ArmVertices;
            m_ArmMesh.triangles = triangles;

            go.AddComponent<MeshFilter>().sharedMesh = m_ArmMesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_SkinMaterial != null ? m_SkinMaterial : new Material(Shader.Find("Standard"));
        }

        void SetVisible(bool visible)
        {
            if (m_VisualRoot != null && m_VisualRoot.gameObject.activeSelf != visible)
                m_VisualRoot.gameObject.SetActive(visible);
        }

        void OnJointsUpdated(XRHandJointsUpdatedEventArgs args)
        {
            var hand = args.hand;
            if (!hand.isTracked)
            {
                SetVisible(false);
                return;
            }

            SetVisible(true);
            UpdateArm(hand);
        }

        void UpdateArm(XRHand hand)
        {
            if (m_Head == null || m_ArmMesh == null || !hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out var wristPose))
                return;

            var space = transform.parent;
            var headPos = space != null ? space.InverseTransformPoint(m_Head.position) : m_Head.position;
            var headFwd = space != null ? space.InverseTransformDirection(m_Head.forward) : m_Head.forward;
            headFwd.y = 0f;
            if (headFwd.sqrMagnitude < 1e-4f)
                headFwd = Vector3.forward;
            var bodyYaw = Quaternion.LookRotation(headFwd.normalized, Vector3.up);
            var side = hand.handedness == Handedness.Left ? -1f : 1f;

            var shoulder = headPos + bodyYaw * new Vector3(side * m_ShoulderWidth, -m_ShoulderDrop, -0.06f);
            // Sink the wrist end slightly into the hand mesh so there is no seam at the cuff.
            var wrist = wristPose.position - wristPose.forward * 0.02f;

            // Two-bone IK with the elbow hanging down, out and slightly back.
            var toWrist = wrist - shoulder;
            var a = m_UpperArmLength;
            var b = m_ForearmLength;
            var reach = Mathf.Clamp(toWrist.magnitude, Mathf.Abs(a - b) + 1e-3f, (a + b) * 0.999f);
            var dir = toWrist.sqrMagnitude > 1e-6f ? toWrist.normalized : bodyYaw * Vector3.forward;
            var along = (a * a - b * b + reach * reach) / (2f * reach);
            var bendHeight = Mathf.Sqrt(Mathf.Max(0f, a * a - along * along));
            var bendDir = Vector3.ProjectOnPlane(bodyYaw * new Vector3(side * 0.6f, -1f, -0.35f), dir);
            if (bendDir.sqrMagnitude < 1e-6f)
                bendDir = Vector3.down;
            var elbow = shoulder + dir * along + bendDir.normalized * bendHeight;

            UpdateArmMesh(shoulder, elbow, wrist);
        }

        /// <summary>Sweeps a tapered tube along shoulder - elbow - wrist, smoothed so the elbow bends rather than kinks.</summary>
        void UpdateArmMesh(Vector3 shoulder, Vector3 elbow, Vector3 wrist)
        {
            // A stable reference direction stops the rings twisting as the arm moves.
            var reference = Vector3.Cross(elbow - shoulder, wrist - elbow);
            if (reference.sqrMagnitude < 1e-8f)
                reference = Vector3.up;
            reference.Normalize();

            for (var i = 0; i < k_Samples; i++)
            {
                var t = i / (float)(k_Samples - 1);
                var centre = QuadraticBezier(shoulder, elbow, wrist, t);
                var ahead = QuadraticBezier(shoulder, elbow, wrist, Mathf.Min(1f, t + 0.01f));
                var behind = QuadraticBezier(shoulder, elbow, wrist, Mathf.Max(0f, t - 0.01f));
                var tangent = (ahead - behind).normalized;

                var right = Vector3.Cross(reference, tangent).normalized;
                var up = Vector3.Cross(tangent, right).normalized;

                // Upper arm is thickest just below the shoulder; the forearm tapers to the wrist.
                var radius = t < 0.5f
                    ? Mathf.Lerp(m_ShoulderRadius, m_ElbowRadius, Mathf.SmoothStep(0f, 1f, t * 2f))
                    : Mathf.Lerp(m_ElbowRadius, m_WristRadius, Mathf.SmoothStep(0f, 1f, (t - 0.5f) * 2f));

                for (var s = 0; s < k_Sides; s++)
                {
                    var angle = s / (float)k_Sides * Mathf.PI * 2f;
                    // Slightly oval, like a real arm rather than a pipe.
                    var normal = (right * (Mathf.Cos(angle) * 1.05f) + up * (Mathf.Sin(angle) * 0.9f)).normalized;
                    var index = i * k_Sides + s;
                    m_ArmVertices[index] = centre + normal * radius;
                    m_ArmNormals[index] = normal;
                }
            }

            var capA = k_Samples * k_Sides;
            m_ArmVertices[capA] = shoulder;
            m_ArmNormals[capA] = (shoulder - elbow).normalized;
            m_ArmVertices[capA + 1] = wrist;
            m_ArmNormals[capA + 1] = (wrist - elbow).normalized;

            m_ArmMesh.vertices = m_ArmVertices;
            m_ArmMesh.normals = m_ArmNormals;
            m_ArmMesh.RecalculateBounds();
        }

        static Vector3 QuadraticBezier(Vector3 p0, Vector3 p1, Vector3 p2, float t)
        {
            var u = 1f - t;
            return u * u * p0 + 2f * u * t * p1 + t * t * p2;
        }
    }
}
