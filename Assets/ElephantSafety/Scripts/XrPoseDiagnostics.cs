using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;

namespace ElephantSafety
{
    /// <summary>
    /// TEMPORARY: logs where the head pose is actually coming from, once a second.
    /// Spawns itself in Play mode so it needs no scene changes. Delete when the rig is sorted.
    /// </summary>
    
    #if false 
    public class XrPoseDiagnostics : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            var go = new GameObject("XR Pose Diagnostics");
            go.AddComponent<XrPoseDiagnostics>();
            DontDestroyOnLoad(go);
        }

        bool m_Captured;

        /// <summary>What is actually near the camera, and what does it see?</summary>
        static void ReportSurroundings(Camera cam)
        {
            var terrain = FindAnyObjectByType<Terrain>();
            var sign = GameObject.Find("Controls Sign");
            var scenery = GameObject.Find("Scenery");
            var renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            var near = 0;
            foreach (var r in renderers)
            {
                if (Vector3.Distance(r.transform.position, cam.transform.position) < 30f)
                    near++;
            }

            Debug.Log($"[XRDIAG] terrain={(terrain != null ? terrain.name + " at " + terrain.transform.position.ToString("F1") : "NONE")} " +
                      $"sign={(sign != null ? sign.transform.position.ToString("F1") : "NONE")} " +
                      $"sceneryChildren={(scenery != null ? scenery.transform.childCount : -1)} " +
                      $"meshRenderers={renderers.Length} within30m={near} " +
                      $"cullingMask=0x{cam.cullingMask:X} near={cam.nearClipPlane} far={cam.farClipPlane}");

            if (terrain != null)
            {
                var p = cam.transform.position;
                Debug.Log($"[XRDIAG] terrainHeightUnderCamera={terrain.SampleHeight(p) + terrain.transform.position.y:F2} " +
                          $"cameraY={p.y:F2} detailDistance={terrain.detailObjectDistance} treeDistance={terrain.treeDistance}");
            }
        }

        static void CaptureView(Camera cam)
        {
            var rt = RenderTexture.GetTemporary(1280, 720, 24);
            var previousTarget = cam.targetTexture;
            var previousActive = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = previousTarget;
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(rt);

            var path = System.IO.Path.Combine(Application.temporaryCachePath, "xrdiag_view.png");
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
            Debug.Log($"[XRDIAG] wrote camera view to {path}");
        }

        IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(3f);

            var origin = FindAnyObjectByType<XROrigin>();

            // Don't rely on Camera.main - the tag may be wrong. Report every camera in the scene.
            foreach (var c in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var d = c.GetComponent<TrackedPoseDriver>();
                Debug.Log($"[XRDIAG] camera '{c.name}' tag={c.tag} enabled={c.enabled} targetEye={c.stereoTargetEye} " +
                          $"depth={c.depth} hasPoseDriver={(d != null)} localPos={c.transform.localPosition:F3} worldPos={c.transform.position:F3}");
            }

            var cam = origin != null && origin.Camera != null ? origin.Camera : FindAnyObjectByType<Camera>();
            var driver = cam != null ? cam.GetComponent<TrackedPoseDriver>() : null;

            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            var inputs = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(inputs);

            Debug.Log($"[XRDIAG] displays={displays.Count} running={(displays.Count > 0 && displays[0].running)} " +
                      $"inputSubsystems={inputs.Count} trackingOriginMode={(inputs.Count > 0 ? inputs[0].GetTrackingOriginMode().ToString() : "n/a")} " +
                      $"supported={(inputs.Count > 0 ? inputs[0].GetSupportedTrackingOriginModes().ToString() : "n/a")}");

            if (origin != null)
                Debug.Log($"[XRDIAG] XROrigin requested={origin.RequestedTrackingOriginMode} current={origin.CurrentTrackingOriginMode} " +
                          $"cameraYOffset={origin.CameraYOffset} offsetObjLocalY={origin.CameraFloorOffsetObject.transform.localPosition.y}");

            if (driver != null)
            {
                var pos = driver.positionInput.action;
                var rot = driver.rotationInput.action;
                Debug.Log($"[XRDIAG] driver ignoreTrackingState={driver.ignoreTrackingState} trackingType={driver.trackingType} " +
                          $"posEnabled={pos?.enabled} posControls={pos?.controls.Count} posControl={(pos != null && pos.controls.Count > 0 ? pos.controls[0].path : "NONE")} " +
                          $"rotControls={rot?.controls.Count}");
            }
            else
            {
                Debug.Log($"[XRDIAG] no TrackedPoseDriver on camera '{(cam != null ? cam.name : "none found")}'");
            }

            // Also read the head pose straight from the device, to compare against the camera transform.
            while (true)
            {
                var head = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
                head.TryGetFeatureValue(CommonUsages.centerEyePosition, out var devicePos);
                var camLocal = cam != null ? cam.transform.localPosition : Vector3.zero;
                var camWorld = cam != null ? cam.transform.position : Vector3.zero;
                var offsetY = origin != null && origin.CameraFloorOffsetObject != null
                    ? origin.CameraFloorOffsetObject.transform.localPosition.y : float.NaN;

                Debug.Log($"[XRDIAG] devicePose={devicePos:F3} camLocal={camLocal:F3} camWorld={camWorld:F3} offsetY={offsetY:F3} " +
                          $"euler={(cam != null ? cam.transform.eulerAngles : Vector3.zero):F1}");

                if (cam != null && !m_Captured)
                {
                    m_Captured = true;
                    ReportSurroundings(cam);
                    CaptureView(cam);
                }

                yield return new WaitForSecondsRealtime(1f);
            }
        }
    }
    #endif 
}
