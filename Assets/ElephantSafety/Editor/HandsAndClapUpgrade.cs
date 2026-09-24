using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.PackageManager.UI;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ElephantSafety.Editor
{
    /// <summary>
    /// One-shot upgrade: real hand meshes, no gesture tint, no clap sound, and a three-clap scare-off.
    /// Patches the existing Demo scene in place so the elephant setup in it is preserved
    /// (unlike <see cref="SavannaHandSimBuilder"/>, which regenerates the scene from scratch).
    /// </summary>
    [InitializeOnLoad]
    public static class HandsAndClapUpgrade
    {
        const string k_Root = "Assets/ElephantSafety";
        const string k_ScenePath = k_Root + "/Scenes/Demo.unity";
        const string k_ResourcesFolder = k_Root + "/Resources";
        const string k_HandsPackage = "com.unity.xr.hands";
        const string k_HandsSample = "HandVisualizer";
        const string k_AnimatorPath = k_Root + "/Elephant/ElephantAnimator.controller";
        const string k_ElephantFbx = k_Root + "/Elephant/AfricanElephant_M_Gameready.fbx";
        const string k_FleeClip = "Loco_Run";
        const string k_FleeTrigger = "Flee";
        const string k_CalmTrigger = "Calm";
        const string k_IdleState = "Stand_00";
        const string k_PendingKey = "ElephantSafety.HandsAndClapUpgrade.Pending";

        static HandsAndClapUpgrade()
        {
            if (!SessionState.GetBool(k_PendingKey, false))
                return;
            SessionState.SetBool(k_PendingKey, false);
            EditorApplication.delayCall += Upgrade;
        }

        public static void UpgradeFromCommandLine() => Upgrade();

        [MenuItem("Elephant Safety/Upgrade Hands And Clap")]
        static void Upgrade()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Exit Play mode before running the upgrade.");
                return;
            }

            if (ImportHandSample(out var importStarted) == null)
            {
                if (importStarted)
                {
                    SessionState.SetBool(k_PendingKey, true);
                    Debug.Log("Importing the XR Hands HandVisualizer sample; the upgrade continues after scripts recompile.");
                }
                return;
            }

            CreateHandPrefabReference();
            AddFleeStateToAnimator();
            PatchScene();
            OptimiseForHeadset();

            AssetDatabase.SaveAssets();
            Debug.Log("Upgrade complete: human hand meshes, no gesture tint, no clap sound, three claps scare the elephant.");
        }

        // ---------------------------------------------------------------- Hand meshes

        static string ImportHandSample(out bool importStarted)
        {
            importStarted = false;
            var samples = Sample.FindByPackage(k_HandsPackage, string.Empty)
                .Where(s => s.displayName == k_HandsSample)
                .ToList();

            if (samples.Count == 0)
            {
                Debug.LogError($"{k_HandsPackage} has no '{k_HandsSample}' sample.");
                return null;
            }

            var sample = samples[0];
            if (!sample.isImported)
            {
                importStarted = sample.Import(Sample.ImportOptions.HideImportWindow | Sample.ImportOptions.OverridePreviousImports);
                return null;
            }

            return sample.importPath;
        }

        static void CreateHandPrefabReference()
        {
            if (!AssetDatabase.IsValidFolder(k_ResourcesFolder))
                AssetDatabase.CreateFolder(k_Root, "Resources");

            var left = FindHandPrefab("Left Hand Tracking");
            var right = FindHandPrefab("Right Hand Tracking");
            if (left == null || right == null)
            {
                Debug.LogError("Could not find the sample's hand prefabs after import.");
                return;
            }

            var path = $"{k_ResourcesFolder}/{HandVisualPrefabs.resourceName}.asset";
            var asset = AssetDatabase.LoadAssetAtPath<HandVisualPrefabs>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<HandVisualPrefabs>();
                AssetDatabase.CreateAsset(asset, path);
            }

            asset.leftHandPrefab = left;
            asset.rightHandPrefab = right;
            EditorUtility.SetDirty(asset);
            Debug.Log($"Hand meshes wired up: {AssetDatabase.GetAssetPath(left)}");
        }

        static GameObject FindHandPrefab(string prefabName) =>
            AssetDatabase.FindAssets($"\"{prefabName}\" t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => Path.GetFileNameWithoutExtension(p) == prefabName)
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .FirstOrDefault(p => p != null);

        // ---------------------------------------------------------------- Elephant animator

        static void AddFleeStateToAnimator()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(k_AnimatorPath);
            if (controller == null)
            {
                Debug.LogError($"No animator controller at {k_AnimatorPath}.");
                return;
            }

            if (!controller.parameters.Any(p => p.name == k_FleeTrigger))
                controller.AddParameter(k_FleeTrigger, AnimatorControllerParameterType.Trigger);

            var machine = controller.layers[0].stateMachine;
            var fleeState = machine.states.FirstOrDefault(s => s.state.name == k_FleeTrigger).state;
            if (fleeState == null)
            {
                fleeState = machine.AddState(k_FleeTrigger);
                fleeState.motion = LoadFleeClip();
                fleeState.speed = 1f;
            }
            else if (fleeState.motion == null)
            {
                fleeState.motion = LoadFleeClip();
            }

            var hasTransition = machine.anyStateTransitions.Any(t => t.destinationState == fleeState);
            if (!hasTransition)
            {
                var transition = machine.AddAnyStateTransition(fleeState);
                transition.AddCondition(AnimatorConditionMode.If, 0f, k_FleeTrigger);
                transition.duration = 0.15f;
                transition.hasExitTime = false;
                transition.canTransitionToSelf = false;
            }

            if (!controller.parameters.Any(p => p.name == k_CalmTrigger))
                controller.AddParameter(k_CalmTrigger, AnimatorControllerParameterType.Trigger);

            // After retreating it settles back to standing rather than running forever.
            var idleState = machine.states.FirstOrDefault(st => st.state.name == k_IdleState).state ?? machine.defaultState;
            if (idleState != null && !fleeState.transitions.Any(t => t.destinationState == idleState))
            {
                var calm = fleeState.AddTransition(idleState);
                calm.AddCondition(AnimatorConditionMode.If, 0f, k_CalmTrigger);
                calm.duration = 0.25f;
                calm.hasExitTime = false;
            }

            EditorUtility.SetDirty(controller);
            Debug.Log($"Animator: '{k_FleeTrigger}' uses {(fleeState.motion != null ? fleeState.motion.name : "NO CLIP")}, " +
                      $"'{k_CalmTrigger}' returns to {(idleState != null ? idleState.name : "NOTHING")}.");
        }

        /// <summary>The run clip lives inside the FBX; make it loop so it keeps running while it retreats.</summary>
        static AnimationClip LoadFleeClip()
        {
            if (AssetImporter.GetAtPath(k_ElephantFbx) is ModelImporter importer)
            {
                var clips = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
                var entry = clips.FirstOrDefault(c => c.name == k_FleeClip);
                if (entry != null && !entry.loopTime)
                {
                    entry.loopTime = true;
                    importer.clipAnimations = clips;
                    importer.SaveAndReimport();
                }
            }

            return AssetDatabase.LoadAllAssetsAtPath(k_ElephantFbx)
                .OfType<AnimationClip>()
                .FirstOrDefault(c => c.name == k_FleeClip);
        }

        // ---------------------------------------------------------------- Performance

        /// <summary>
        /// Trims the scene for headset framerates. The savanna was built for a desktop view:
        /// 1000 m view distance, grass to 70 m, soft shadows everywhere and ~1100 separate renderers.
        /// </summary>
        static void OptimiseForHeadset()
        {
            var scene = EditorSceneManager.GetActiveScene();

            // Draw distances: fog hides the cut, so the far plane can come in a long way.
            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                cam.farClipPlane = 350f;
                cam.allowHDR = false;
            }

            var terrain = Object.FindAnyObjectByType<Terrain>();
            if (terrain != null)
            {
                terrain.heightmapPixelError = 12f;      // fewer terrain triangles
                terrain.basemapDistance = 120f;
                terrain.detailObjectDistance = 32f;     // grass is the big cost
                terrain.detailObjectDensity = 0.55f;
                terrain.treeDistance = 250f;
                terrain.treeBillboardDistance = 60f;
                terrain.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            // Small scenery does not need to cast shadows; trees keep theirs.
            var shadowsOff = 0;
            foreach (var renderer in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var name = renderer.gameObject.name.ToLowerInvariant();
                if (name.Contains("bush") || name.Contains("lobe") || name.Contains("mound") || name.Contains("boulder") || name.Contains("rock"))
                {
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    shadowsOff++;
                }
            }

            // One directional light with hard, short-range shadows is much cheaper in stereo.
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional)
                    light.shadows = LightShadows.Hard;
            }

            // GPU instancing on the repeated scenery materials.
            var instanced = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { k_Root + "/Materials" }))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (mat != null && !mat.enableInstancing)
                {
                    mat.enableInstancing = true;
                    EditorUtility.SetDirty(mat);
                    instanced++;
                }
            }

            // Skinned meshes: the elephant and hands do not need off-screen updates or many bones per vertex.
            foreach (var skin in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                skin.updateWhenOffscreen = false;
                skin.quality = SkinQuality.Bone2;
            }

            // Project-wide quality: no vsync (the compositor paces us), shorter shadows, fewer cascades.
            QualitySettings.vSyncCount = 0;
            QualitySettings.shadowDistance = 45f;
            QualitySettings.shadowCascades = 1;
            QualitySettings.shadowResolution = ShadowResolution.Low;
            QualitySettings.softParticles = false;
            QualitySettings.antiAliasing = 2;
            QualitySettings.lodBias = 0.8f;
            QualitySettings.skinWeights = SkinWeights.TwoBones;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"Headset optimisation: far clip 350 m, grass to 32 m, shadows 45 m / hard / 1 cascade, " +
                      $"{shadowsOff} scenery renderers stop casting shadows, {instanced} materials instanced.");
        }

        // ---------------------------------------------------------------- Scene

        static void PatchScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != k_ScenePath)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return;
                scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            }

            var changed = false;

            // The clap no longer plays a sound, so drop the audio source it used.
            foreach (var clap in Object.FindObjectsByType<ClapDetector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var source = clap.GetComponent<AudioSource>();
                if (source != null)
                {
                    Object.DestroyImmediate(source);
                    changed = true;
                    Debug.Log("Removed the clap AudioSource.");
                }
            }

            // Pose text labels are gone; delete the objects the builder made for them.
            foreach (var text in Object.FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (text.gameObject.name == "Pose Label")
                {
                    Object.DestroyImmediate(text.gameObject);
                    changed = true;
                }
            }

            // Give the elephant its flee behaviour. The zone detector sits on a separate marker
            // object ("ZoneZenter"), so find the model by its animator controller instead.
            var animatorAsset = AssetDatabase.LoadAssetAtPath<AnimatorController>(k_AnimatorPath);
            var elephant = Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(a => a.runtimeAnimatorController == animatorAsset)?.gameObject;

            // Clean up any ElephantFlee that landed on the wrong object.
            foreach (var stray in Object.FindObjectsByType<ElephantFlee>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (stray.gameObject != elephant)
                {
                    Debug.Log($"Removed ElephantFlee from '{stray.gameObject.name}' (not the elephant model).");
                    Object.DestroyImmediate(stray);
                    changed = true;
                }
            }

            if (elephant == null)
            {
                Debug.LogWarning("No object using ElephantAnimator found; add ElephantFlee to the elephant by hand.");
            }
            else if (elephant.GetComponent<ElephantFlee>() == null)
            {
                elephant.AddComponent<ElephantFlee>();
                changed = true;
                Debug.Log($"Added ElephantFlee to '{elephant.name}' at {elephant.transform.position}.");
            }
            else
            {
                Debug.Log($"ElephantFlee already on '{elephant.name}'.");
            }

            if (changed)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
        }
    }
}
