using System.Collections;
using UnityEngine;

namespace ElephantSafety
{
    /// <summary>
    /// Backs the elephant off when the player claps three times from the comfort zone - the real-world
    /// farm technique. It moves a set distance away and settles again rather than leaving the scene.
    /// Put this on the elephant model; it finds the <see cref="ClapDetector"/> and zone detector itself.
    /// </summary>
    public class ElephantFlee : MonoBehaviour
    {
        [SerializeField, Tooltip("Left empty, the ClapDetector in the scene is used.")]
        ClapDetector m_ClapDetector;

        [SerializeField, Tooltip("Left empty, the Animator on this object (or its children) is used.")]
        Animator m_Animator;

        [SerializeField, Tooltip("Left empty, the ElephantZoneDetector in the scene is used.")]
        ElephantZoneDetector m_ZoneDetector;

        [SerializeField, Tooltip("Animator trigger that starts the run.")]
        string m_RunTrigger = "Flee";

        [SerializeField, Tooltip("Animator trigger that returns it to standing once it has moved away.")]
        string m_CalmTrigger = "Calm";

        [SerializeField, Tooltip("Who it moves away from. Left empty, the main camera is used.")]
        Transform m_Player;

        [Header("Retreat")]
        [SerializeField, Tooltip("Clapping only works from this zone - too close and it will not back off.")]
        bool m_RequireComfortZone = true;

        [SerializeField, Tooltip("How far it moves away, in metres.")]
        float m_RetreatDistance = 20f;

        [SerializeField, Tooltip("Metres per second while retreating.")]
        float m_RunSpeed = 5f;

        [SerializeField, Tooltip("Degrees per second while turning away.")]
        float m_TurnSpeed = 120f;

        public bool isRetreating { get; private set; }

        void Awake()
        {
            if (m_Animator == null)
                m_Animator = GetComponentInChildren<Animator>();
            if (m_ClapDetector == null)
                m_ClapDetector = FindAnyObjectByType<ClapDetector>();

            // The zone detector usually lives on a separate marker object, not on the model.
            if (m_ZoneDetector == null)
                m_ZoneDetector = GetComponent<ElephantZoneDetector>() ?? FindAnyObjectByType<ElephantZoneDetector>();
        }

        void OnEnable()
        {
            if (m_ClapDetector != null)
                m_ClapDetector.sequenceCompleted.AddListener(OnClapSequence);
            else
                Debug.LogWarning("[ElephantFlee] No ClapDetector found; clapping will not move the elephant.", this);
        }

        void OnDisable()
        {
            if (m_ClapDetector != null)
                m_ClapDetector.sequenceCompleted.RemoveListener(OnClapSequence);
        }

        void OnClapSequence()
        {
            if (isRetreating)
                return;

            // Clapping works from a safe distance. Close up, the elephant is already agitated.
            if (m_RequireComfortZone && m_ZoneDetector != null && m_ZoneDetector.CurrentZone != ElephantZone.Comfort)
            {
                Debug.Log($"[ElephantFlee] Clapped from the {m_ZoneDetector.CurrentZone} zone - " +
                          "back off to the comfort zone and clap again.");
                return;
            }

            Retreat();
        }

        /// <summary>Also callable from a UnityEvent or the Inspector context menu, for testing.</summary>
        [ContextMenu("Retreat")]
        public void Retreat()
        {
            if (isRetreating || !isActiveAndEnabled)
                return;
            StartCoroutine(RetreatRoutine());
        }

        IEnumerator RetreatRoutine()
        {
            isRetreating = true;
            Debug.Log($"[ElephantFlee] Three claps - elephant is moving {m_RetreatDistance} m away.");

            // Keep the zone triggers quiet while it walks off, so it doesn't turn and attack mid-retreat.
            var zoneWasEnabled = m_ZoneDetector != null && m_ZoneDetector.enabled;
            if (m_ZoneDetector != null)
                m_ZoneDetector.enabled = false;

            var player = m_Player != null ? m_Player : (Camera.main != null ? Camera.main.transform : null);

            var away = player != null ? transform.position - player.position : transform.forward;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-4f)
                away = transform.forward;
            var targetRotation = Quaternion.LookRotation(away.normalized, Vector3.up);

            if (m_Animator != null && !string.IsNullOrEmpty(m_RunTrigger))
                m_Animator.SetTrigger(m_RunTrigger);

            while (Quaternion.Angle(transform.rotation, targetRotation) > 5f)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, m_TurnSpeed * Time.deltaTime);
                yield return null;
            }

            var start = transform.position;
            var terrain = Terrain.activeTerrain;
            while (Vector3.Distance(start, transform.position) < m_RetreatDistance)
            {
                transform.position += transform.forward * (m_RunSpeed * Time.deltaTime);

                // Follow the ground so it doesn't run through the dunes.
                if (terrain != null)
                {
                    var p = transform.position;
                    p.y = terrain.SampleHeight(p) + terrain.transform.position.y;
                    transform.position = p;
                }

                yield return null;
            }

            // Settle: face the player again and go back to standing. The elephant stays in the scene.
            if (m_Animator != null && !string.IsNullOrEmpty(m_CalmTrigger))
                m_Animator.SetTrigger(m_CalmTrigger);

            if (player != null)
            {
                var back = player.position - transform.position;
                back.y = 0f;
                if (back.sqrMagnitude > 1e-4f)
                {
                    var facePlayer = Quaternion.LookRotation(back.normalized, Vector3.up);
                    while (Quaternion.Angle(transform.rotation, facePlayer) > 5f)
                    {
                        transform.rotation = Quaternion.RotateTowards(transform.rotation, facePlayer, m_TurnSpeed * 0.5f * Time.deltaTime);
                        yield return null;
                    }
                }
            }

            if (m_ZoneDetector != null)
                m_ZoneDetector.enabled = zoneWasEnabled;

            isRetreating = false;
            Debug.Log("[ElephantFlee] Elephant has settled at a safer distance.");
        }
    }
}
