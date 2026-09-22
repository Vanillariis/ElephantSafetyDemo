using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Gestures;

namespace ElephantSafety
{
    /// <summary>
    /// Checks a prioritised list of XR Hands <see cref="XRHandShape"/> / <see cref="XRHandPose"/> assets every
    /// joint update and reports which one the hand is currently holding.
    /// </summary>
    [RequireComponent(typeof(XRHandTrackingEvents))]
    public class HandPoseDetector : MonoBehaviour
    {
        [Serializable]
        public class PoseEntry
        {
            public string displayName;

            [Tooltip("Hand pose (shape + orientation). Checked instead of the shape when set.")]
            public XRHandPose handPose;

            public XRHandShape handShape;

            public Color highlight = Color.cyan;

            public bool Check(XRHandJointsUpdatedEventArgs args)
            {
                if (handPose != null)
                    return handPose.CheckConditions(args);
                return handShape != null && handShape.CheckConditions(args);
            }
        }

        [Serializable]
        public class PoseChangedEvent : UnityEvent<Handedness, string> { }

        [SerializeField, Tooltip("Checked top to bottom; the first match wins.")]
        List<PoseEntry> m_Poses = new();

        [SerializeField, Tooltip("How long a pose must be held before it counts as detected.")]
        float m_MinimumHoldTime = 0.15f;

        [SerializeField, Tooltip("Seconds between checks. Gesture checks are cheap, but no need to run them every frame.")]
        float m_CheckInterval = 0.05f;

        [SerializeField]
        TextMesh m_Label;

        [SerializeField]
        HandArmVisual m_Visual;

        [SerializeField]
        PoseChangedEvent m_PoseChanged = new();

        XRHandTrackingEvents m_TrackingEvents;
        PoseEntry m_Candidate;
        PoseEntry m_Current;
        float m_CandidateSince;
        float m_LastCheckTime;
        float m_Highlight;

        public List<PoseEntry> poses => m_Poses;

        public TextMesh label
        {
            get => m_Label;
            set => m_Label = value;
        }

        public HandArmVisual visual
        {
            get => m_Visual;
            set => m_Visual = value;
        }

        public PoseChangedEvent poseChanged => m_PoseChanged;

        /// <summary>Name of the currently detected pose, or null when none.</summary>
        public string currentPose => m_Current?.displayName;

        void Awake()
        {
            m_TrackingEvents = GetComponent<XRHandTrackingEvents>();
        }

        void OnEnable()
        {
            m_TrackingEvents.jointsUpdated.AddListener(OnJointsUpdated);
            m_TrackingEvents.trackingChanged.AddListener(OnTrackingChanged);
            OnTrackingChanged(false);
        }

        void OnDisable()
        {
            m_TrackingEvents.jointsUpdated.RemoveListener(OnJointsUpdated);
            m_TrackingEvents.trackingChanged.RemoveListener(OnTrackingChanged);
        }

        void Update()
        {
            var target = m_Current != null ? 0.55f : 0f;
            m_Highlight = Mathf.MoveTowards(m_Highlight, target, Time.deltaTime * 4f);
            if (m_Visual != null)
                m_Visual.SetHighlight(m_Current?.highlight ?? Color.white, m_Highlight);
        }

        void OnTrackingChanged(bool tracked)
        {
            if (m_Label != null)
                m_Label.gameObject.SetActive(tracked);
            if (!tracked)
                SetCurrent(null);
        }

        void OnJointsUpdated(XRHandJointsUpdatedEventArgs args)
        {
            UpdateLabelTransform(args.hand);

            if (Time.unscaledTime - m_LastCheckTime < m_CheckInterval)
                return;
            m_LastCheckTime = Time.unscaledTime;

            PoseEntry match = null;
            foreach (var entry in m_Poses)
            {
                if (entry.Check(args))
                {
                    match = entry;
                    break;
                }
            }

            if (match != m_Candidate)
            {
                m_Candidate = match;
                m_CandidateSince = Time.unscaledTime;
            }

            if (m_Candidate != m_Current && Time.unscaledTime - m_CandidateSince >= m_MinimumHoldTime)
                SetCurrent(m_Candidate);
        }

        void SetCurrent(PoseEntry entry)
        {
            if (entry == m_Current)
                return;

            m_Current = entry;
            var poseName = entry?.displayName;
            var handedness = m_TrackingEvents != null ? m_TrackingEvents.handedness : Handedness.Invalid;

            if (m_Label != null)
            {
                m_Label.text = poseName ?? "-";
                m_Label.color = entry?.highlight ?? new Color(1f, 1f, 1f, 0.6f);
            }

            if (poseName != null)
                Debug.Log($"[HandPoseDetector] {handedness} hand: {poseName}");

            m_PoseChanged.Invoke(handedness, poseName);
        }

        void UpdateLabelTransform(XRHand hand)
        {
            if (m_Label == null || !hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out var wrist))
                return;

            var space = transform.parent;
            var worldPos = space != null ? space.TransformPoint(wrist.position) : wrist.position;
            var labelTransform = m_Label.transform;
            labelTransform.position = worldPos + Vector3.up * 0.14f;

            var cam = Camera.main;
            if (cam != null)
                labelTransform.rotation = Quaternion.LookRotation(labelTransform.position - cam.transform.position, Vector3.up);
        }
    }
}
