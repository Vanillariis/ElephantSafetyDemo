using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Management;

namespace ElephantSafety
{
    /// <summary>
    /// Picks between a real headset and the XR Interaction Simulator when Play starts.
    /// With a headset running, the simulator is switched off so it can't replace the real head and hands;
    /// without one, the simulator is put straight into hand mode so there's no need to press ] twice.
    /// Tag this object EditorOnly.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    public class SimulatorHandModeStarter : MonoBehaviour
    {
        bool m_HeadsetActive;

        void Awake()
        {
            // XR Management starts the headset before the scene loads, so this is already known.
            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            m_HeadsetActive = manager != null && manager.activeLoader != null;
            if (!m_HeadsetActive)
                return;

            var simulator = FindAnyObjectByType<XRInteractionSimulator>(FindObjectsInactive.Include);
            if (simulator != null)
            {
                simulator.transform.root.gameObject.SetActive(false);
                Debug.Log($"Headset detected ({manager.activeLoader.name}); XR Interaction Simulator disabled.");
            }
        }

        IEnumerator Start()
        {
            if (m_HeadsetActive)
                yield break;

            // The simulator registers its devices over the first few frames.
            for (var i = 0; i < 10 && SimulatedDeviceLifecycleManager.instance == null; i++)
                yield return null;
            yield return null;

            var lifecycle = SimulatedDeviceLifecycleManager.instance;
            if (lifecycle == null || lifecycle.deviceMode != SimulatedDeviceLifecycleManager.DeviceMode.Controller)
                yield break;

            // SwitchDeviceMode is internal in XRI 3.x; it is what the ] / [ hotkeys call.
            var switchMode = typeof(SimulatedDeviceLifecycleManager).GetMethod("SwitchDeviceMode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (switchMode == null)
            {
                Debug.LogWarning("Could not switch the XR Interaction Simulator to hand mode automatically. Press ] twice to switch.");
                yield break;
            }

            switchMode.Invoke(lifecycle, null);
        }
    }
}
