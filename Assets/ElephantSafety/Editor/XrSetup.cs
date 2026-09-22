using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.Hands.OpenXR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace ElephantSafety.Editor
{
    /// <summary>
    /// Turns on OpenXR with hand tracking for PC (Quest Link / any PC OpenXR runtime) and Android (Quest builds),
    /// which is the part of XR Plug-in Management that otherwise has to be ticked by hand.
    /// </summary>
    public static class XrSetup
    {
        const string k_SettingsAssetPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
        const string k_OpenXRLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";

        public static void EnableFromCommandLine() => Enable();

        [MenuItem("Elephant Safety/Enable OpenXR Hand Tracking")]
        static void Enable()
        {
            var perBuildTarget = GetOrCreatePerBuildTargetSettings();

            EnableForGroup(perBuildTarget, BuildTargetGroup.Standalone, android: false);
            EnableForGroup(perBuildTarget, BuildTargetGroup.Android, android: true);
            ConfigureAndroidPlayerSettings();
            ForceDirect3D11OnWindows();

            EditorUtility.SetDirty(perBuildTarget);
            AssetDatabase.SaveAssets();
            Debug.Log("OpenXR + hand tracking enabled for Windows and Android. Restart Play mode for it to take effect.");
        }

        static XRGeneralSettingsPerBuildTarget GetOrCreatePerBuildTargetSettings()
        {
            if (EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget existing) && existing != null)
                return existing;

            var asset = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(k_SettingsAssetPath);
            if (asset == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(k_SettingsAssetPath));
                asset = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(asset, k_SettingsAssetPath);
            }

            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, asset, true);
            return asset;
        }

        static void EnableForGroup(XRGeneralSettingsPerBuildTarget perBuildTarget, BuildTargetGroup group, bool android)
        {
            if (!perBuildTarget.HasManagerSettingsForBuildTarget(group))
                perBuildTarget.CreateDefaultManagerSettingsForBuildTarget(group);

            var settings = perBuildTarget.SettingsForBuildTarget(group);
            if (settings == null || settings.Manager == null)
            {
                Debug.LogError($"Could not create XR settings for {group}.");
                return;
            }

            settings.InitManagerOnStart = true;
            if (!XRPackageMetadataStore.AssignLoader(settings.Manager, k_OpenXRLoader, group))
                Debug.LogError($"Could not assign the OpenXR loader for {group}.");

            // Features live in per-build-target settings that only exist once refreshed.
            FeatureHelpers.RefreshFeatures(group);
            var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (openXr == null)
            {
                Debug.LogError($"No OpenXR settings for {group}.");
                return;
            }

            var wanted = new List<Type>
            {
                typeof(HandTracking),                 // XR Hands: the joint data this project reads
                typeof(HandCommonPosesInteraction),   // grip/pinch/poke poses for hands
                typeof(HandInteractionProfile),       // hand interaction profile
                typeof(OculusTouchControllerProfile), // so controllers still work
            };
            if (android)
            {
                wanted.Add(typeof(MetaQuestFeature));    // Quest manifest entries etc.
                wanted.Add(typeof(MetaHandTrackingAim)); // Meta's pinch/aim data
            }

            var features = openXr.GetFeatures();
            foreach (var type in wanted)
            {
                var feature = features.FirstOrDefault(f => f != null && type.IsInstanceOfType(f));
                if (feature == null)
                {
                    Debug.LogWarning($"OpenXR feature {type.Name} is not available for {group}.");
                    continue;
                }

                feature.enabled = true;
                EditorUtility.SetDirty(feature);
            }

            EditorUtility.SetDirty(openXr);
            EditorUtility.SetDirty(settings);
        }

        /// <summary>
        /// Meta's Link OpenXR runtime is unreliable when Unity submits frames over D3D12 - the headset shows a
        /// black or frozen image while the desktop renders correctly. D3D11 is the supported path.
        /// </summary>
        static void ForceDirect3D11OnWindows()
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 });
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows, new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 });
        }

        /// <summary>Quest headsets are 64-bit Android devices; these are the minimums for a build to install.</summary>
        static void ConfigureAndroidPlayerSettings()
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            if (PlayerSettings.Android.minSdkVersion < AndroidSdkVersions.AndroidApiLevel29)
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        }
    }
}
