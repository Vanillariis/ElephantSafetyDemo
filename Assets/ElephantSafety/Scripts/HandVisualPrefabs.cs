using UnityEngine;

namespace ElephantSafety
{
    /// <summary>
    /// Points at the skinned hand prefabs from the XR Hands "HandVisualizer" sample.
    /// Stored in Resources so <see cref="HandArmVisual"/> can find them without a scene reference,
    /// which keeps the Demo scene's own wiring untouched when the hands are upgraded.
    /// </summary>
    public class HandVisualPrefabs : ScriptableObject
    {
        public const string resourceName = "ElephantSafetyHandPrefabs";

        [SerializeField] GameObject m_LeftHandPrefab;
        [SerializeField] GameObject m_RightHandPrefab;

        public GameObject leftHandPrefab { get => m_LeftHandPrefab; set => m_LeftHandPrefab = value; }
        public GameObject rightHandPrefab { get => m_RightHandPrefab; set => m_RightHandPrefab = value; }

        public static HandVisualPrefabs Load() => Resources.Load<HandVisualPrefabs>(resourceName);
    }
}
