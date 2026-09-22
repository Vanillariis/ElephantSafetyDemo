using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace ElephantSafety
{
    /// <summary>
    /// Draws a stylised hand (spheres + cylinders on every XR Hands joint) and a two-bone arm
    /// that reaches from an estimated shoulder to the tracked wrist.
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
        [SerializeField] float m_UpperArmRadius = 0.042f;
        [SerializeField] float m_ForearmRadius = 0.032f;

        [Header("Hand proportions (metres)")]
        [SerializeField] float m_KnuckleRadius = 0.0105f;
        [SerializeField] float m_TipRadius = 0.0075f;

        static readonly (XRHandJointID from, XRHandJointID to)[] s_Bones =
        {
            (XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal),
            (XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal),
            (XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip),

            (XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate),
            (XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal),
            (XRHandJointID.IndexDistal, XRHandJointID.IndexTip),

            (XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate),
            (XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal),
            (XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip),

            (XRHandJointID.RingProximal, XRHandJointID.RingIntermediate),
            (XRHandJointID.RingIntermediate, XRHandJointID.RingDistal),
            (XRHandJointID.RingDistal, XRHandJointID.RingTip),

            (XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate),
            (XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal),
            (XRHandJointID.LittleDistal, XRHandJointID.LittleTip),

            // Palm fan: wrist to the thumb base and every knuckle, plus the knuckle row.
            (XRHandJointID.Wrist, XRHandJointID.ThumbMetacarpal),
            (XRHandJointID.Wrist, XRHandJointID.IndexProximal),
            (XRHandJointID.Wrist, XRHandJointID.MiddleProximal),
            (XRHandJointID.Wrist, XRHandJointID.RingProximal),
            (XRHandJointID.Wrist, XRHandJointID.LittleProximal),
            (XRHandJointID.IndexProximal, XRHandJointID.MiddleProximal),
            (XRHandJointID.MiddleProximal, XRHandJointID.RingProximal),
            (XRHandJointID.RingProximal, XRHandJointID.LittleProximal),
        };

        XRHandTrackingEvents m_TrackingEvents;
        Transform m_VisualRoot;
        Material m_MaterialInstance;
        Color m_BaseColor;

        readonly Dictionary<XRHandJointID, Transform> m_JointSpheres = new();
        readonly List<Transform> m_BoneCylinders = new();
        readonly Vector3[] m_JointPositions = new Vector3[XRHandJointID.EndMarker.ToIndex()];
        readonly bool[] m_JointValid = new bool[XRHandJointID.EndMarker.ToIndex()];

        Transform m_Shoulder, m_Elbow, m_UpperArm, m_Forearm, m_PalmPad;

        public Transform head
        {
            get => m_Head;
            set => m_Head = value;
        }

        public Material skinMaterial
        {
            get => m_SkinMaterial;
            set => m_SkinMaterial = value;
        }

        void Awake()
        {
            m_TrackingEvents = GetComponent<XRHandTrackingEvents>();
            m_MaterialInstance = m_SkinMaterial != null ? new Material(m_SkinMaterial) : new Material(Shader.Find("Standard"));
            m_BaseColor = m_MaterialInstance.color;
            if (m_Head == null && Camera.main != null)
                m_Head = Camera.main.transform;
            BuildVisuals();
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
            if (m_MaterialInstance != null)
                Destroy(m_MaterialInstance);
        }

        /// <summary>Tints the whole hand and arm, e.g. as feedback when a pose is detected.</summary>
        public void SetHighlight(Color color, float amount)
        {
            if (m_MaterialInstance != null)
                m_MaterialInstance.color = Color.Lerp(m_BaseColor, color, amount);
        }

        void BuildVisuals()
        {
            m_VisualRoot = new GameObject("Visual").transform;
            m_VisualRoot.SetParent(transform, false);

            for (var id = XRHandJointID.BeginMarker; id < XRHandJointID.EndMarker; id++)
            {
                if (id == XRHandJointID.Palm)
                    continue;
                var sphere = CreatePart(PrimitiveType.Sphere, id.ToString());
                sphere.localScale = Vector3.one * (JointRadius(id) * 2f);
                m_JointSpheres[id] = sphere;
            }

            foreach (var bone in s_Bones)
                m_BoneCylinders.Add(CreatePart(PrimitiveType.Cylinder, $"{bone.from}-{bone.to}"));

            m_PalmPad = CreatePart(PrimitiveType.Sphere, "Palm Pad");
            m_Shoulder = CreatePart(PrimitiveType.Sphere, "Shoulder");
            m_Elbow = CreatePart(PrimitiveType.Sphere, "Elbow");
            m_UpperArm = CreatePart(PrimitiveType.Cylinder, "Upper Arm");
            m_Forearm = CreatePart(PrimitiveType.Cylinder, "Forearm");
            m_Shoulder.localScale = Vector3.one * (m_UpperArmRadius * 2.1f);
            m_Elbow.localScale = Vector3.one * (Mathf.Lerp(m_UpperArmRadius, m_ForearmRadius, 0.5f) * 2f);
        }

        Transform CreatePart(PrimitiveType type, string partName)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = partName;
            Destroy(go.GetComponent<Collider>());
            go.GetComponent<MeshRenderer>().sharedMaterial = m_MaterialInstance;
            go.transform.SetParent(m_VisualRoot, false);
            return go.transform;
        }

        float JointRadius(XRHandJointID id)
        {
            switch (id)
            {
                case XRHandJointID.Wrist:
                    return m_ForearmRadius * 0.85f;
                case XRHandJointID.ThumbMetacarpal:
                    return m_KnuckleRadius * 1.25f;
                case XRHandJointID.ThumbTip:
                case XRHandJointID.IndexTip:
                case XRHandJointID.MiddleTip:
                case XRHandJointID.RingTip:
                case XRHandJointID.LittleTip:
                    return m_TipRadius;
                case XRHandJointID.ThumbDistal:
                case XRHandJointID.IndexDistal:
                case XRHandJointID.MiddleDistal:
                case XRHandJointID.RingDistal:
                case XRHandJointID.LittleDistal:
                    return Mathf.Lerp(m_TipRadius, m_KnuckleRadius, 0.35f);
                case XRHandJointID.IndexIntermediate:
                case XRHandJointID.MiddleIntermediate:
                case XRHandJointID.RingIntermediate:
                case XRHandJointID.LittleIntermediate:
                    return Mathf.Lerp(m_TipRadius, m_KnuckleRadius, 0.7f);
                case XRHandJointID.LittleMetacarpal:
                case XRHandJointID.LittleProximal:
                    return m_KnuckleRadius * 0.85f;
                default:
                    return m_KnuckleRadius;
            }
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

            // Joint poses are in XR Origin session space; this object sits at identity under the Camera Offset.
            for (var id = XRHandJointID.BeginMarker; id < XRHandJointID.EndMarker; id++)
            {
                var index = id.ToIndex();
                m_JointValid[index] = hand.GetJoint(id).TryGetPose(out var pose);
                if (!m_JointValid[index])
                    continue;

                m_JointPositions[index] = pose.position;
                if (m_JointSpheres.TryGetValue(id, out var sphere))
                    sphere.localPosition = pose.position;

                if (id == XRHandJointID.Palm)
                {
                    m_PalmPad.localPosition = pose.position;
                    m_PalmPad.localRotation = pose.rotation;
                    m_PalmPad.localScale = new Vector3(0.075f, 0.026f, 0.08f);
                }
            }

            for (var i = 0; i < s_Bones.Length; i++)
            {
                var (from, to) = s_Bones[i];
                var valid = m_JointValid[from.ToIndex()] && m_JointValid[to.ToIndex()];
                if (m_BoneCylinders[i].gameObject.activeSelf != valid)
                    m_BoneCylinders[i].gameObject.SetActive(valid);
                if (!valid)
                    continue;

                var radius = from == XRHandJointID.Wrist ? m_KnuckleRadius * 1.3f : Mathf.Min(JointRadius(from), JointRadius(to));
                PlaceSegment(m_BoneCylinders[i], m_JointPositions[from.ToIndex()], m_JointPositions[to.ToIndex()], radius);
            }

            UpdateArm(hand);
        }

        void UpdateArm(XRHand hand)
        {
            if (m_Head == null || !hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out var wristPose))
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

            // End the forearm slightly behind the wrist joint so it tucks into the hand.
            var forearmEnd = wristPose.position - wristPose.forward * 0.015f;

            // Two-bone IK with the elbow hanging down, out and slightly back.
            var toWrist = forearmEnd - shoulder;
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

            m_Shoulder.localPosition = shoulder;
            m_Elbow.localPosition = elbow;
            PlaceSegment(m_UpperArm, shoulder, elbow, m_UpperArmRadius);
            PlaceSegment(m_Forearm, elbow, forearmEnd, m_ForearmRadius);
        }

        static void PlaceSegment(Transform cylinder, Vector3 from, Vector3 to, float radius)
        {
            var delta = to - from;
            var length = delta.magnitude;
            cylinder.localPosition = (from + to) * 0.5f;
            cylinder.localRotation = length > 1e-5f ? Quaternion.FromToRotation(Vector3.up, delta / length) : Quaternion.identity;
            // Unity's cylinder mesh is 2 units tall and 1 unit wide.
            cylinder.localScale = new Vector3(radius * 2f, length * 0.5f, radius * 2f);
        }
    }
}
