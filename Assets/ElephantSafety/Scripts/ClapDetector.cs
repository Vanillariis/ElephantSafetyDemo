using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Gestures;

namespace ElephantSafety
{
    /// <summary>
    /// Detects a clap: two flat hands, palms facing each other, closing quickly until they meet.
    /// A clap needs both hands, so unlike the single-hand poses it can't be an <see cref="XRHandShape"/>.
    /// </summary>
    public class ClapDetector : MonoBehaviour
    {
        [SerializeField] XRHandTrackingEvents m_LeftHand;
        [SerializeField] XRHandTrackingEvents m_RightHand;

        [Header("Thresholds")]
        [SerializeField, Tooltip("Palms closer than this count as a strike.")]
        float m_ClapDistance = 0.1f;

        [SerializeField, Tooltip("Hands must separate by this much before another clap registers.")]
        float m_ReleaseDistance = 0.22f;

        [SerializeField, Tooltip("How fast the palms must be closing, in metres per second, so resting hands together doesn't count.")]
        float m_MinApproachSpeed = 0.45f;

        [SerializeField, Range(0f, 180f), Tooltip("How far the palms may be from facing each other.")]
        float m_MaxPalmAngle = 75f;

        [SerializeField, Range(0f, 1f), Tooltip("Maximum finger curl: a clap uses flat hands, not fists.")]
        float m_MaxFingerCurl = 0.6f;

        [Header("Clap sequence")]
        [SerializeField, Tooltip("How many claps in a row raise the sequence event - three claps is the scare-off signal.")]
        int m_ClapsInSequence = 3;

        [SerializeField, Tooltip("Maximum gap between claps before the count restarts.")]
        float m_SequenceGap = 2f;

        [Header("Feedback")]
        [SerializeField] TextMesh m_Label;
        [SerializeField] float m_LabelHoldTime = 1.2f;

        [SerializeField]
        UnityEvent<int> m_Clapped = new();

        [SerializeField, Tooltip("Raised when the full sequence of claps is completed.")]
        UnityEvent m_SequenceCompleted = new();

        struct HandSample
        {
            public bool valid;
            public float time;
            public Vector3 palm;
            public Vector3 normal;
            public float curl;
        }

        HandSample m_Left, m_Right;
        float m_PreviousDistance = -1f;
        float m_PreviousDistanceTime;
        bool m_HandsApart = true;
        float m_LabelHideTime;
        int m_SequenceCount;
        float m_LastClapTime = float.NegativeInfinity;

        public UnityEvent<int> clapped => m_Clapped;

        /// <summary>Raised when <see cref="clapsInSequence"/> claps happen within the allowed gap.</summary>
        public UnityEvent sequenceCompleted => m_SequenceCompleted;

        /// <summary>How many claps of the current sequence have landed so far.</summary>
        public int sequenceCount => m_SequenceCount;

        public int clapsInSequence { get => m_ClapsInSequence; set => m_ClapsInSequence = value; }

        /// <summary>Number of claps detected since the scene started.</summary>
        public int clapCount { get; private set; }

        public XRHandTrackingEvents leftHand { get => m_LeftHand; set => m_LeftHand = value; }
        public XRHandTrackingEvents rightHand { get => m_RightHand; set => m_RightHand = value; }
        public TextMesh label { get => m_Label; set => m_Label = value; }

        void Awake()
        {
            if (m_Label != null)
                m_Label.gameObject.SetActive(false);
        }

        void OnEnable()
        {
            if (m_LeftHand != null)
                m_LeftHand.jointsUpdated.AddListener(OnLeftUpdated);
            if (m_RightHand != null)
                m_RightHand.jointsUpdated.AddListener(OnRightUpdated);
        }

        void OnDisable()
        {
            if (m_LeftHand != null)
                m_LeftHand.jointsUpdated.RemoveListener(OnLeftUpdated);
            if (m_RightHand != null)
                m_RightHand.jointsUpdated.RemoveListener(OnRightUpdated);
        }

        void Update()
        {
            if (m_Label != null && m_Label.gameObject.activeSelf && Time.unscaledTime > m_LabelHideTime)
                m_Label.gameObject.SetActive(false);
        }

        void OnLeftUpdated(XRHandJointsUpdatedEventArgs args)
        {
            m_Left = Sample(args.hand);
            Evaluate();
        }

        void OnRightUpdated(XRHandJointsUpdatedEventArgs args)
        {
            m_Right = Sample(args.hand);
            Evaluate();
        }

        static HandSample Sample(XRHand hand)
        {
            var sample = new HandSample { time = Time.unscaledTime };
            if (!hand.isTracked ||
                !hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out var wrist) ||
                !hand.GetJoint(XRHandJointID.IndexProximal).TryGetPose(out var indexBase) ||
                !hand.GetJoint(XRHandJointID.LittleProximal).TryGetPose(out var littleBase))
            {
                return sample;
            }

            // Palm joint is optional on some providers, so fall back to the middle of wrist and knuckles.
            sample.palm = hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palmPose)
                ? palmPose.position
                : (wrist.position + indexBase.position + littleBase.position) / 3f;

            // Palm plane normal, worked out from the joints so it doesn't depend on a provider's
            // joint rotation conventions. Which side it points out of varies between hands and
            // providers, so IsClap only uses the axis and ignores the sign.
            sample.normal = Vector3.Cross(indexBase.position - wrist.position, littleBase.position - wrist.position).normalized;

            var index = hand.CalculateFingerShape(XRHandFingerID.Index, XRFingerShapeTypes.FullCurl);
            var middle = hand.CalculateFingerShape(XRHandFingerID.Middle, XRFingerShapeTypes.FullCurl);
            index.TryGetFullCurl(out var indexCurl);
            middle.TryGetFullCurl(out var middleCurl);
            sample.curl = Mathf.Max(indexCurl, middleCurl);

            sample.valid = true;
            return sample;
        }

        void Evaluate()
        {
            var now = Time.unscaledTime;

            // Both hands must have reported recently; otherwise wait for the other one.
            if (!m_Left.valid || !m_Right.valid || now - m_Left.time > 0.2f || now - m_Right.time > 0.2f)
            {
                m_PreviousDistance = -1f;
                return;
            }

            var distance = Vector3.Distance(m_Left.palm, m_Right.palm);
            var deltaTime = now - m_PreviousDistanceTime;
            var approachSpeed = m_PreviousDistance >= 0f && deltaTime > 0.0001f
                ? (m_PreviousDistance - distance) / deltaTime
                : 0f;
            m_PreviousDistance = distance;
            m_PreviousDistanceTime = now;

            if (distance > m_ReleaseDistance)
                m_HandsApart = true;

            if (!m_HandsApart)
                return;

            if (!IsClap(m_Left.palm, m_Right.palm, m_Left.normal, m_Right.normal, m_Left.curl, m_Right.curl,
                    approachSpeed, m_ClapDistance, m_MaxPalmAngle, m_MaxFingerCurl, m_MinApproachSpeed))
            {
                return;
            }

            m_HandsApart = false;
            OnClap((m_Left.palm + m_Right.palm) * 0.5f);
        }

        /// <summary>The clap test itself, split out so it can be checked without live hand data.</summary>
        public static bool IsClap(
            Vector3 leftPalm, Vector3 rightPalm, Vector3 leftNormal, Vector3 rightNormal,
            float leftCurl, float rightCurl, float approachSpeed,
            float clapDistance, float maxPalmAngle, float maxFingerCurl, float minApproachSpeed)
        {
            var delta = rightPalm - leftPalm;
            var distance = delta.magnitude;
            if (distance > clapDistance || approachSpeed < minApproachSpeed)
                return false;

            if (leftCurl > maxFingerCurl || rightCurl > maxFingerCurl)
                return false;

            // Palms have to meet face to face, not merely be near each other: the two palm planes
            // roughly parallel, and the line between them roughly along the palm normals.
            // Magnitudes only, since which way a palm normal points varies by hand and provider.
            var cosLimit = Mathf.Cos(maxPalmAngle * Mathf.Deg2Rad);
            if (Mathf.Abs(Vector3.Dot(leftNormal, rightNormal)) < cosLimit)
                return false;

            if (distance > 0.02f)
            {
                var toRight = delta / distance;
                if (Mathf.Abs(Vector3.Dot(leftNormal, toRight)) < cosLimit ||
                    Mathf.Abs(Vector3.Dot(rightNormal, toRight)) < cosLimit)
                {
                    return false;
                }
            }

            return true;
        }

        void OnClap(Vector3 position)
        {
            clapCount++;

            // Claps must follow each other closely to count as one sequence.
            m_SequenceCount = Time.unscaledTime - m_LastClapTime <= m_SequenceGap ? m_SequenceCount + 1 : 1;
            m_LastClapTime = Time.unscaledTime;
            var completed = m_SequenceCount >= m_ClapsInSequence;

            Debug.Log($"[ClapDetector] Clap #{clapCount} ({m_SequenceCount}/{m_ClapsInSequence})" + (completed ? " - sequence complete" : ""));

            if (m_Label != null)
            {
                m_Label.text = completed ? "CLAP!" : $"CLAP {m_SequenceCount}/{m_ClapsInSequence}";
                m_Label.transform.position = position + Vector3.up * 0.2f;
                var cam = Camera.main;
                if (cam != null)
                    m_Label.transform.rotation = Quaternion.LookRotation(m_Label.transform.position - cam.transform.position, Vector3.up);
                m_Label.gameObject.SetActive(true);
                m_LabelHideTime = Time.unscaledTime + m_LabelHoldTime;
            }

            m_Clapped.Invoke(clapCount);

            if (completed)
            {
                m_SequenceCount = 0;
                m_LastClapTime = float.NegativeInfinity;
                m_SequenceCompleted.Invoke();
            }
        }

    }
}
