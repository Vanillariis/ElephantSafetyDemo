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

        [Header("Feedback")]
        [SerializeField] TextMesh m_Label;
        [SerializeField] AudioSource m_AudioSource;
        [SerializeField] float m_LabelHoldTime = 1.2f;

        [SerializeField]
        UnityEvent<int> m_Clapped = new();

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
        AudioClip m_ClapClip;

        public UnityEvent<int> clapped => m_Clapped;

        /// <summary>Number of claps detected since the scene started.</summary>
        public int clapCount { get; private set; }

        public XRHandTrackingEvents leftHand { get => m_LeftHand; set => m_LeftHand = value; }
        public XRHandTrackingEvents rightHand { get => m_RightHand; set => m_RightHand = value; }
        public TextMesh label { get => m_Label; set => m_Label = value; }
        public AudioSource audioSource { get => m_AudioSource; set => m_AudioSource = value; }

        void Awake()
        {
            m_ClapClip = CreateClapClip();
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
            Debug.Log($"[ClapDetector] Clap #{clapCount}");

            if (m_AudioSource != null && m_ClapClip != null)
            {
                m_AudioSource.transform.position = position;
                m_AudioSource.PlayOneShot(m_ClapClip);
            }

            if (m_Label != null)
            {
                m_Label.text = clapCount > 1 ? $"CLAP x{clapCount}" : "CLAP!";
                m_Label.transform.position = position + Vector3.up * 0.2f;
                var cam = Camera.main;
                if (cam != null)
                    m_Label.transform.rotation = Quaternion.LookRotation(m_Label.transform.position - cam.transform.position, Vector3.up);
                m_Label.gameObject.SetActive(true);
                m_LabelHideTime = Time.unscaledTime + m_LabelHoldTime;
            }

            m_Clapped.Invoke(clapCount);
        }

        /// <summary>A short noise burst with a fast decay - enough of a clap without shipping an audio file.</summary>
        static AudioClip CreateClapClip()
        {
            const int sampleRate = 44100;
            const int samples = sampleRate / 5;
            var data = new float[samples];
            var random = new System.Random(11);

            for (var i = 0; i < samples; i++)
            {
                var t = i / (float)samples;
                var attack = Mathf.Clamp01(i / (sampleRate * 0.002f));
                var decay = Mathf.Exp(-14f * t);
                var noise = (float)(random.NextDouble() * 2.0 - 1.0);
                data[i] = noise * attack * decay * 0.7f;
            }

            var clip = AudioClip.Create("Clap", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
