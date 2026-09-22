using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Gestures;

namespace ElephantSafety.Editor
{
    /// <summary>
    /// Builds the savanna hand-tracking test scene: a grassy savanna plain, an XR Origin with procedural hands + arms,
    /// XR Hands pose detection, and the XR Interaction Simulator so it runs without a headset.
    /// </summary>
    [InitializeOnLoad]
    public static class SavannaHandSimBuilder
    {
        const string k_Root = "Assets/ElephantSafety";
        const string k_ScenePath = k_Root + "/Scenes/Demo.unity";
        const string k_PendingBuildKey = "ElephantSafety.SavannaHandSim.PendingBuild";
        const string k_XriPackage = "com.unity.xr.interaction.toolkit";
        const string k_SimulatorSample = "XR Interaction Simulator";

        static SavannaHandSimBuilder()
        {
            // Second half of a build that had to import the simulator sample (which triggers a script reload).
            if (SessionState.GetBool(k_PendingBuildKey, false))
            {
                SessionState.SetBool(k_PendingBuildKey, false);
                EditorApplication.delayCall += Build;
            }
        }

        /// <summary>
        /// Batch-mode entry point: <c>-executeMethod ElephantSafety.Editor.SavannaHandSimBuilder.BuildFromCommandLine</c>.
        /// Run it twice on a fresh project: the first run imports the simulator sample, the second builds the scene.
        /// </summary>
        public static void BuildFromCommandLine() => Build();

        [MenuItem("Elephant Safety/Build Demo Scene")]
        static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Exit Play mode before building the Demo scene.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var simulatorPrefab = FindOrImportSimulatorPrefab(out var importStarted);
            if (importStarted)
            {
                SessionState.SetBool(k_PendingBuildKey, true);
                Debug.Log("Imported the XR Interaction Simulator sample. The scene will finish building after scripts recompile.");
                return;
            }

            EnsureFolder(k_Root + "/Scenes");
            EnsureFolder(k_Root + "/Materials");
            EnsureFolder(k_Root + "/Textures");
            EnsureFolder(k_Root + "/Terrain");
            EnsureFolder(k_Root + "/Gestures");
            EnsureFolder(k_Root + "/Prefabs");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            DeleteObsoleteDesertAssets();

            var sun = CreateLighting();
            CreateSavanna();
            var camera = CreateXROriginWithHands(out var cameraOffset);
            CreateInstructionSign();

            if (simulatorPrefab != null)
            {
                // EditorOnly keeps the simulator out of headset builds.
                var simulator = (GameObject)PrefabUtility.InstantiatePrefab(simulatorPrefab, scene);
                simulator.tag = "EditorOnly";
                var starter = new GameObject("Simulator Hand Mode Starter") { tag = "EditorOnly" };
                starter.AddComponent<SimulatorHandModeStarter>();
            }
            else
                Debug.LogWarning($"Could not find the '{k_SimulatorSample}' sample prefab. Import it from Package Manager > XR Interaction Toolkit > Samples, then rebuild.");

            Selection.activeObject = camera.gameObject;
            RenderSettings.sun = sun;

            EditorSceneManager.SaveScene(scene, k_ScenePath);
            AddSceneToBuildSettings(k_ScenePath);
            AssetDatabase.SaveAssets();

            Debug.Log($"Built {k_ScenePath}. Press Play; the simulator starts in hand mode.");
        }

        // ---------------------------------------------------------------- Simulator

        static GameObject FindOrImportSimulatorPrefab(out bool importStarted)
        {
            importStarted = false;
            var samples = Sample.FindByPackage(k_XriPackage, string.Empty)
                .Where(s => s.displayName == k_SimulatorSample)
                .ToList();

            if (samples.Count == 0)
            {
                Debug.LogError($"{k_XriPackage} is not installed, so the hand simulator is unavailable.");
                return null;
            }

            var sample = samples[0];
            if (!sample.isImported)
            {
                importStarted = sample.Import(Sample.ImportOptions.HideImportWindow | Sample.ImportOptions.OverridePreviousImports);
                if (importStarted)
                    return null;
            }

            var guid = AssetDatabase.FindAssets($"\"{k_SimulatorSample}\" t:Prefab")
                .FirstOrDefault(g => Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g)) == k_SimulatorSample);
            return guid != null ? AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)) : null;
        }

        // ---------------------------------------------------------------- Lighting & sky

        static Light CreateLighting()
        {
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.93f, 0.8f);
            sun.intensity = 1.3f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.8f;
            sunGo.transform.rotation = Quaternion.Euler(38f, -40f, 0f);

            var sky = new Material(Shader.Find("Skybox/Procedural"));
            sky.SetFloat("_SunSize", 0.04f);
            sky.SetFloat("_SunSizeConvergence", 5f);
            sky.SetFloat("_AtmosphereThickness", 0.95f);
            sky.SetColor("_SkyTint", new Color(0.48f, 0.6f, 0.82f));
            sky.SetColor("_GroundColor", new Color(0.55f, 0.5f, 0.38f));
            sky.SetFloat("_Exposure", 1.25f);
            sky = SaveMaterial(sky, "Savanna Sky");

            RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1.05f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.0025f;
            RenderSettings.fogColor = new Color(0.8f, 0.8f, 0.74f);
            return sun;
        }

        // ---------------------------------------------------------------- Savanna

        const float k_TerrainSize = 600f;
        const float k_TerrainHeight = 50f;
        const float k_TerrainBaseY = -2f;
        const float k_PlainRadius = 150f;
        const float k_ClearingRadius = 2.5f;

        // Rocky outcrops (kopjes) on the plain: x, z, radius, height.
        static readonly Vector4[] s_Kopjes =
        {
            new(-95f, 120f, 16f, 7f),
            new(135f, 70f, 12f, 5f),
            new(40f, -160f, 20f, 9f),
        };

        static void CreateSavanna()
        {
            var grassLayer = CreateOrReplaceAsset(new TerrainLayer
            {
                diffuseTexture = CreateGroundTexture("Savanna_Grass_Albedo.png", new Color(0.66f, 0.58f, 0.33f), new Color(0.45f, 0.43f, 0.22f), 11),
                tileSize = new Vector2(5f, 5f),
            }, k_Root + "/Terrain/Savanna Grass.terrainlayer");

            var dirtLayer = CreateOrReplaceAsset(new TerrainLayer
            {
                diffuseTexture = CreateGroundTexture("Savanna_Dirt_Albedo.png", new Color(0.64f, 0.44f, 0.29f), new Color(0.46f, 0.31f, 0.21f), 23),
                tileSize = new Vector2(4f, 4f),
            }, k_Root + "/Terrain/Savanna Dirt.terrainlayer");

            // TerrainData keeps its splat maps as sub-assets, so recreate it instead of copying over the old one.
            const string terrainPath = k_Root + "/Terrain/Savanna.asset";
            AssetDatabase.DeleteAsset(terrainPath);
            var data = new TerrainData { heightmapResolution = 513, alphamapResolution = 512 };
            data.size = new Vector3(k_TerrainSize, k_TerrainHeight, k_TerrainSize);
            AssetDatabase.CreateAsset(data, terrainPath);
            data.terrainLayers = new[] { grassLayer, dirtLayer };

            var res = data.heightmapResolution;
            var heights = new float[res, res];
            for (var y = 0; y < res; y++)
            {
                for (var x = 0; x < res; x++)
                {
                    var (wx, wz) = CellToWorld(x, y, res);
                    heights[y, x] = Mathf.Clamp01((SavannaHeight(wx, wz) - k_TerrainBaseY) / k_TerrainHeight);
                }
            }
            data.SetHeights(0, 0, heights);

            var aRes = data.alphamapResolution;
            var alphas = new float[aRes, aRes, 2];
            for (var y = 0; y < aRes; y++)
            {
                for (var x = 0; x < aRes; x++)
                {
                    var (wx, wz) = CellToWorld(x, y, aRes);
                    var dirt = DirtAmount(wx, wz);
                    alphas[y, x, 0] = 1f - dirt;
                    alphas[y, x, 1] = dirt;
                }
            }
            data.SetAlphamaps(0, 0, alphas);

            // Tall dry grass, painted as terrain details and kept out of the clearing and dirt tracks.
            data.detailPrototypes = new[]
            {
                new DetailPrototype
                {
                    prototypeTexture = CreateGrassBladeTexture(),
                    renderMode = DetailRenderMode.Grass,
                    usePrototypeMesh = false,
                    healthyColor = new Color(0.86f, 0.79f, 0.5f),
                    dryColor = new Color(0.74f, 0.61f, 0.36f),
                    minWidth = 0.6f,
                    maxWidth = 1.1f,
                    minHeight = 0.35f,
                    maxHeight = 0.85f,
                    noiseSpread = 0.4f,
                },
            };
            data.SetDetailResolution(1024, 32);
            // New terrains default to coverage mode (0-255); the layer below stores instances per cell.
            data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
            data.wavingGrassTint = new Color(0.82f, 0.76f, 0.52f);
            data.wavingGrassStrength = 0.3f;
            data.wavingGrassAmount = 0.25f;
            data.wavingGrassSpeed = 0.4f;

            var dRes = data.detailResolution;
            var grassDensity = new int[dRes, dRes];
            for (var y = 0; y < dRes; y++)
            {
                for (var x = 0; x < dRes; x++)
                {
                    var (wx, wz) = CellToWorld(x, y, dRes);
                    var noise = Mathf.PerlinNoise(wx * 0.09f + 7f, wz * 0.09f + 51f);
                    var amount = (1f - DirtAmount(wx, wz)) * (0.35f + 0.65f * noise);
                    grassDensity[y, x] = Mathf.RoundToInt(amount * 5f);
                }
            }
            data.SetDetailLayer(0, 0, 0, grassDensity);
            EditorUtility.SetDirty(data);

            var terrainGo = Terrain.CreateTerrainGameObject(data);
            terrainGo.name = "Savanna Plain";
            terrainGo.transform.position = new Vector3(-k_TerrainSize * 0.5f, k_TerrainBaseY, -k_TerrainSize * 0.5f);
            var terrain = terrainGo.GetComponent<Terrain>();
            terrain.heightmapPixelError = 3f;
            terrain.basemapDistance = 300f;
            terrain.detailObjectDistance = 70f;
            terrain.detailObjectDensity = 1f;
            terrain.drawInstanced = true;

            ScatterScenery(terrain);
        }

        static (float x, float z) CellToWorld(int x, int y, int resolution) =>
            (x / (float)(resolution - 1) * k_TerrainSize - k_TerrainSize * 0.5f,
             y / (float)(resolution - 1) * k_TerrainSize - k_TerrainSize * 0.5f);

        /// <summary>World-space height in metres: gently rolling grassland, flat where you stand, hills on the horizon.</summary>
        static float SavannaHeight(float x, float z)
        {
            var distance = Mathf.Sqrt(x * x + z * z);

            var rolling = (Mathf.PerlinNoise(x * 0.012f + 3.3f, z * 0.012f + 8.1f) - 0.5f) * 1.6f
                + (Mathf.PerlinNoise(x * 0.06f + 17f, z * 0.06f + 2f) - 0.5f) * 0.25f;
            rolling *= Mathf.InverseLerp(4f, 30f, distance);

            var hills = Mathf.Pow(Mathf.PerlinNoise(x * 0.008f + 60f, z * 0.008f + 11f), 1.5f) * 34f
                + Mathf.PerlinNoise(x * 0.03f, z * 0.03f) * 4f;
            var hillMask = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(k_PlainRadius, k_PlainRadius + 110f, distance));

            var kopjes = 0f;
            foreach (var k in s_Kopjes)
            {
                var dx = x - k.x;
                var dz = z - k.y;
                kopjes += k.w * Mathf.Exp(-(dx * dx + dz * dz) / (k.z * k.z * 0.35f));
            }

            return rolling + hills * hillMask + kopjes;
        }

        /// <summary>0 = grass, 1 = bare red earth: scattered patches, a clearing where you stand, and a winding game track.</summary>
        static float DirtAmount(float x, float z)
        {
            var patches = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.62f, 0.78f, Mathf.PerlinNoise(x * 0.035f + 101f, z * 0.035f + 37f)));

            var distance = Mathf.Sqrt(x * x + z * z);
            var clearing = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(k_ClearingRadius, k_ClearingRadius + 3f, distance));

            var trackOffset = Mathf.Abs(z - 9f - Mathf.Sin(x * 0.02f) * 12f);
            var track = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.7f, 1.8f, trackOffset));

            return Mathf.Clamp01(Mathf.Max(patches, Mathf.Max(clearing * 0.9f, track * 0.9f)));
        }

        static void ScatterScenery(Terrain terrain)
        {
            var bark = SaveMaterial(StandardMaterial(new Color(0.3f, 0.24f, 0.18f), 0.08f), "Acacia Bark");
            var leaves = SaveMaterial(StandardMaterial(new Color(0.33f, 0.4f, 0.16f), 0.05f), "Acacia Leaves");
            var bushMat = SaveMaterial(StandardMaterial(new Color(0.27f, 0.33f, 0.15f), 0.05f), "Bush");
            var moundMat = SaveMaterial(StandardMaterial(new Color(0.6f, 0.37f, 0.24f), 0.05f), "Termite Mound");
            var rockMat = SaveMaterial(StandardMaterial(new Color(0.5f, 0.45f, 0.4f), 0.12f), "Kopje Rock");

            var acacia = SavePrefab(BuildAcacia(bark, leaves), "Acacia Tree");
            var bush = SavePrefab(BuildBush(bushMat), "Bush");
            var mound = SavePrefab(BuildTermiteMound(moundMat), "Termite Mound");

            var parent = new GameObject("Scenery").transform;
            var random = new System.Random(2024);
            float Range(float min, float max) => min + (float)random.NextDouble() * (max - min);

            Vector3 GroundPoint(float x, float z)
            {
                var p = new Vector3(x, 0f, z);
                p.y = terrain.SampleHeight(p) + terrain.transform.position.y;
                return p;
            }

            void Scatter(GameObject prefab, int count, float minDistance, float maxDistance, float minScale, float maxScale)
            {
                for (var i = 0; i < count; i++)
                {
                    var angle = Range(0f, Mathf.PI * 2f);
                    var distance = Mathf.Lerp(minDistance, maxDistance, Mathf.Sqrt(Range(0f, 1f)));
                    var x = Mathf.Cos(angle) * distance;
                    var z = Mathf.Sin(angle) * distance;
                    if (Mathf.Abs(x) < 3f && z > 0f && z < 6f)
                        continue; // keep the view of the sign clear

                    var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    go.transform.SetParent(parent, false);
                    go.transform.position = GroundPoint(x, z) - new Vector3(0f, 0.05f, 0f);
                    go.transform.rotation = Quaternion.Euler(0f, Range(0f, 360f), 0f);
                    go.transform.localScale = Vector3.one * Range(minScale, maxScale);
                    GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
                }
            }

            Scatter(acacia, 55, 14f, 260f, 0.75f, 1.3f);
            Scatter(bush, 140, 5f, 200f, 0.6f, 1.6f);
            Scatter(mound, 14, 9f, 120f, 0.7f, 1.4f);

            foreach (var k in s_Kopjes)
            {
                for (var i = 0; i < 8; i++)
                {
                    var angle = Range(0f, Mathf.PI * 2f);
                    var offset = Range(0f, k.z * 0.45f);
                    var size = Range(2f, 6f);
                    var rock = Part(PrimitiveType.Sphere, "Kopje Boulder", parent, rockMat);
                    rock.localScale = new Vector3(size * Range(1f, 1.6f), size * Range(0.6f, 1f), size * Range(1f, 1.5f));
                    rock.rotation = Quaternion.Euler(Range(-15f, 15f), Range(0f, 360f), Range(-15f, 15f));
                    rock.position = GroundPoint(k.x + Mathf.Cos(angle) * offset, k.y + Mathf.Sin(angle) * offset);
                    GameObjectUtility.SetStaticEditorFlags(rock.gameObject, StaticEditorFlags.BatchingStatic);
                }
            }
        }

        static GameObject BuildAcacia(Material bark, Material leaves)
        {
            var root = new GameObject("Acacia Tree");
            var fork = new Vector3(0.35f, 3.1f, 0.1f);
            PlaceSegment(Part(PrimitiveType.Cylinder, "Trunk", root.transform, bark), Vector3.zero, fork, 0.17f);

            var tips = new[]
            {
                new Vector3(-1.9f, 4.5f, 0.9f),
                new Vector3(2.2f, 4.7f, -0.6f),
                new Vector3(0.4f, 4.9f, 1.9f),
                new Vector3(0.6f, 4.6f, -1.8f),
            };
            foreach (var tip in tips)
            {
                PlaceSegment(Part(PrimitiveType.Cylinder, "Branch", root.transform, bark), fork, tip, 0.08f);
                var pad = Part(PrimitiveType.Sphere, "Canopy", root.transform, leaves);
                pad.localPosition = tip + new Vector3(0f, 0.35f, 0f);
                pad.localScale = new Vector3(3f, 0.55f, 2.6f);
                pad.localRotation = Quaternion.Euler(0f, tip.x * 25f, 0f);
            }

            var top = Part(PrimitiveType.Sphere, "Canopy Top", root.transform, leaves);
            top.localPosition = new Vector3(0.3f, 5.05f, 0.1f);
            top.localScale = new Vector3(4.4f, 0.7f, 3.9f);
            return root;
        }

        static GameObject BuildBush(Material material)
        {
            var root = new GameObject("Bush");
            var lobes = new[]
            {
                (new Vector3(0f, 0.35f, 0f), new Vector3(1.3f, 0.8f, 1.1f)),
                (new Vector3(0.5f, 0.25f, 0.3f), new Vector3(0.9f, 0.6f, 0.9f)),
                (new Vector3(-0.45f, 0.22f, -0.25f), new Vector3(0.8f, 0.55f, 0.85f)),
            };
            foreach (var (position, scale) in lobes)
            {
                var lobe = Part(PrimitiveType.Sphere, "Lobe", root.transform, material);
                lobe.localPosition = position;
                lobe.localScale = scale;
            }
            return root;
        }

        static GameObject BuildTermiteMound(Material material)
        {
            var root = new GameObject("Termite Mound");
            var parts = new[]
            {
                (PrimitiveType.Sphere, new Vector3(0f, 0.1f, 0f), new Vector3(1.7f, 0.6f, 1.6f)),
                (PrimitiveType.Capsule, new Vector3(0f, 0.9f, 0f), new Vector3(0.9f, 1.05f, 0.85f)),
                (PrimitiveType.Capsule, new Vector3(0.15f, 2.1f, 0.05f), new Vector3(0.45f, 0.75f, 0.45f)),
            };
            foreach (var (type, position, scale) in parts)
            {
                var part = Part(type, "Mound", root.transform, material);
                part.localPosition = position;
                part.localScale = scale;
            }
            return root;
        }

        static Transform Part(PrimitiveType type, string name, Transform parent, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static void PlaceSegment(Transform cylinder, Vector3 from, Vector3 to, float radius)
        {
            var delta = to - from;
            cylinder.localPosition = (from + to) * 0.5f;
            cylinder.localRotation = Quaternion.FromToRotation(Vector3.up, delta.normalized);
            cylinder.localScale = new Vector3(radius * 2f, delta.magnitude * 0.5f, radius * 2f);
        }

        static Texture2D CreateGroundTexture(string fileName, Color light, Color dark, int seed)
        {
            const int size = 512;
            var tex = new Texture2D(size, size, TextureFormat.RGB24, true);
            var random = new System.Random(seed);

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = x / (float)size;
                    var v = y / (float)size;
                    var n = TileableNoise(u, v, 4, seed) * 0.5f + TileableNoise(u, v, 16, seed) * 0.3f + TileableNoise(u, v, 64, seed) * 0.2f;
                    var grain = (float)random.NextDouble() * 0.16f - 0.08f;
                    tex.SetPixel(x, y, Color.Lerp(dark, light, Mathf.Clamp01(n + grain)));
                }
            }

            return SaveTexture(tex, fileName, false);
        }

        /// <summary>A clump of dry grass blades on a transparent background, used for terrain detail grass.</summary>
        static Texture2D CreateGrassBladeTexture()
        {
            const int size = 256;
            var pixels = new Color32[size * size];
            // Transparent pixels keep the grass colour so mipmaps don't fringe dark.
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(200, 180, 110, 0);

            var random = new System.Random(5);
            float Range(float min, float max) => min + (float)random.NextDouble() * (max - min);

            for (var blade = 0; blade < 24; blade++)
            {
                var baseX = Range(20f, size - 20f);
                var lean = Range(-60f, 60f);
                var heightPx = Mathf.RoundToInt(Range(0.55f, 1f) * (size - 2));
                var baseWidth = Range(3f, 7f);
                var color = Color.Lerp(new Color(0.84f, 0.75f, 0.47f), new Color(0.6f, 0.55f, 0.28f), Range(0f, 1f));

                for (var y = 0; y < heightPx; y++)
                {
                    var t = y / (float)heightPx;
                    var cx = baseX + lean * t * t;
                    var halfWidth = baseWidth * (1f - t) * 0.5f + 0.4f;
                    var shade = Mathf.Lerp(0.75f, 1.1f, t);
                    for (var x = Mathf.FloorToInt(cx - halfWidth - 1f); x <= Mathf.CeilToInt(cx + halfWidth + 1f); x++)
                    {
                        if (x < 0 || x >= size)
                            continue;
                        var alpha = Mathf.Clamp01(halfWidth - Mathf.Abs(x - cx) + 0.5f);
                        var index = y * size + x;
                        if (alpha * 255f <= pixels[index].a)
                            continue;
                        pixels[index] = new Color(color.r * shade, color.g * shade, color.b * shade, alpha);
                    }
                }
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            tex.SetPixels32(pixels);
            return SaveTexture(tex, "Savanna_Grass_Blades.png", true);
        }

        /// <summary>Value noise that wraps seamlessly at u, v = 0..1.</summary>
        static float TileableNoise(float u, float v, int period, int seed)
        {
            var fx = u * period;
            var fy = v * period;
            var x0 = Mathf.FloorToInt(fx);
            var y0 = Mathf.FloorToInt(fy);
            var tx = Mathf.SmoothStep(0f, 1f, fx - x0);
            var ty = Mathf.SmoothStep(0f, 1f, fy - y0);

            float Lattice(int x, int y)
            {
                x = ((x % period) + period) % period;
                y = ((y % period) + period) % period;
                unchecked
                {
                    var hash = (uint)(x * 374761393 + y * 668265263 + period * 982451653 + seed * 1013904223);
                    hash = (hash ^ (hash >> 13)) * 1274126177u;
                    return (hash ^ (hash >> 16)) / (float)uint.MaxValue;
                }
            }

            var a = Mathf.Lerp(Lattice(x0, y0), Lattice(x0 + 1, y0), tx);
            var b = Mathf.Lerp(Lattice(x0, y0 + 1), Lattice(x0 + 1, y0 + 1), tx);
            return Mathf.Lerp(a, b, ty);
        }

        // ---------------------------------------------------------------- XR Origin, hands, gestures

        static Camera CreateXROriginWithHands(out Transform cameraOffset)
        {
            var originGo = new GameObject("XR Origin (Hands)");
            var origin = originGo.AddComponent<XROrigin>();

            // Pre-set standing height: without a headset XROrigin never applies CameraYOffset,
            // and with one in Floor mode it resets this to 0 on its own.
            cameraOffset = new GameObject("Camera Offset").transform;
            cameraOffset.SetParent(originGo.transform, false);
            cameraOffset.localPosition = new Vector3(0f, 1.6f, 0f);

            var cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
            cameraGo.transform.SetParent(cameraOffset, false);
            var camera = cameraGo.AddComponent<Camera>();
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.Skybox;
            cameraGo.AddComponent<AudioListener>();

            var poseDriver = cameraGo.AddComponent<TrackedPoseDriver>();
            poseDriver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            poseDriver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            poseDriver.positionInput = new InputActionProperty(CreateAction("Head Position", "Vector3", "<XRHMD>/centerEyePosition", "<HandheldARInputDevice>/devicePosition"));
            poseDriver.rotationInput = new InputActionProperty(CreateAction("Head Rotation", "Quaternion", "<XRHMD>/centerEyeRotation", "<HandheldARInputDevice>/deviceRotation"));
            poseDriver.trackingStateInput = new InputActionProperty(CreateAction("Head Tracking State", "Integer", "<XRHMD>/trackingState"));
            // Apply the head pose even if the tracking-state control doesn't resolve on a given runtime.
            // Otherwise the driver refuses to move the camera and it sits at the origin (floor level).
            poseDriver.ignoreTrackingState = true;

            origin.Origin = originGo;
            origin.CameraFloorOffsetObject = cameraOffset.gameObject;
            origin.Camera = camera;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.NotSpecified;
            origin.CameraYOffset = 1.6f;

            var skin = SaveMaterial(StandardMaterial(new Color(0.86f, 0.66f, 0.54f), 0.32f), "Hand Skin");

            var poses = CreateGestureAssets();
            var leftHand = CreateHand(Handedness.Left, cameraOffset, camera.transform, skin, poses);
            var rightHand = CreateHand(Handedness.Right, cameraOffset, camera.transform, skin, poses);
            CreateClapDetector(cameraOffset, leftHand, rightHand);
            return camera;
        }

        static InputAction CreateAction(string name, string controlType, params string[] bindings)
        {
            var action = new InputAction(name, InputActionType.Value, expectedControlType: controlType);
            foreach (var binding in bindings)
                action.AddBinding(binding);
            return action;
        }

        static XRHandTrackingEvents CreateHand(Handedness handedness, Transform parent, Transform head, Material skin, List<HandPoseDetector.PoseEntry> poses)
        {
            var handGo = new GameObject($"{handedness} Hand");
            handGo.transform.SetParent(parent, false);

            var events = handGo.AddComponent<XRHandTrackingEvents>();
            events.handedness = handedness;

            var visual = handGo.AddComponent<HandArmVisual>();
            visual.head = head;
            visual.skinMaterial = skin;

            var labelGo = new GameObject("Pose Label");
            labelGo.transform.SetParent(handGo.transform, false);
            var label = CreateTextMesh(labelGo, "-", 0.0035f, 60);

            var detector = handGo.AddComponent<HandPoseDetector>();
            detector.visual = visual;
            detector.label = label;
            detector.poses.AddRange(poses);
            return events;
        }

        /// <summary>Clapping needs both hands at once, so it gets its own detector rather than a hand shape asset.</summary>
        static void CreateClapDetector(Transform parent, XRHandTrackingEvents left, XRHandTrackingEvents right)
        {
            var go = new GameObject("Clap Detector");
            go.transform.SetParent(parent, false);

            var labelGo = new GameObject("Clap Label");
            labelGo.transform.SetParent(go.transform, false);
            var label = CreateTextMesh(labelGo, "CLAP!", 0.006f, 64);
            label.color = new Color(1f, 0.85f, 0.35f);

            var audioSource = go.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 1f;
            audioSource.minDistance = 0.5f;
            audioSource.maxDistance = 20f;

            var clap = go.AddComponent<ClapDetector>();
            clap.leftHand = left;
            clap.rightHand = right;
            clap.label = label;
            clap.audioSource = audioSource;
        }

        static List<HandPoseDetector.PoseEntry> CreateGestureAssets()
        {
            const string folder = k_Root + "/Gestures";

            // Thresholds start from the example shapes in the XR Hands "Gestures" sample, loosened where
            // the XR Interaction Simulator's captured poses fell just outside them (checked in Play mode).
            // No thumb condition on Fist: a thumb resting across the fingers varies a lot, and
            // Thumbs Up / Thumb Out are checked first to catch an extended thumb.
            var fist = CreateShape(folder, "Fist Shape",
                Curl(XRHandFingerID.Index, 1f, 0.2f, 0f),
                Curl(XRHandFingerID.Middle, 1f, 0.2f, 0f),
                Curl(XRHandFingerID.Ring, 1f, 0.2f, 0f),
                Curl(XRHandFingerID.Little, 1f, 0.3f, 0f));

            var grab = CreateShape(folder, "Grab Shape",
                Curl(XRHandFingerID.Index, 0.5f, 0.15f, 0.15f),
                Curl(XRHandFingerID.Middle, 0.5f, 0.15f, 0.15f),
                Curl(XRHandFingerID.Ring, 0.5f, 0.15f, 0.15f),
                Curl(XRHandFingerID.Little, 0.5f, 0.25f, 0.25f),
                Curl(XRHandFingerID.Thumb, 0.5f, 0.375f, 0.375f));

            var open = CreateShape(folder, "Open Palm Shape",
                Curl(XRHandFingerID.Index, 0f, 0.25f, 0.35f),
                Curl(XRHandFingerID.Thumb, 0f, 0.4f, 0.4f),
                Curl(XRHandFingerID.Middle, 0f, 0.25f, 0.35f),
                Curl(XRHandFingerID.Ring, 0f, 0.25f, 0.35f),
                Curl(XRHandFingerID.Little, 0f, 0.25f, 0.25f));

            var point = CreateShape(folder, "Point Shape",
                Curl(XRHandFingerID.Index, 0f, 0f, 0.35f),
                Curl(XRHandFingerID.Middle, 1f, 0.25f, 0.25f),
                Curl(XRHandFingerID.Little, 1f, 0.25f, 0.25f));

            var shaka = CreateShape(folder, "Shaka Shape",
                Curl(XRHandFingerID.Thumb, 0f, 0.2f, 0.3f),
                Curl(XRHandFingerID.Index, 1f, 0.3f, 0.25f),
                Curl(XRHandFingerID.Middle, 1f, 0.3f, 0.2f),
                Curl(XRHandFingerID.Ring, 1f, 0.4f, 0.2f),
                Curl(XRHandFingerID.Little, 0f, 0.3f, 0.4f));

            var thumb = CreateShape(folder, "Thumb Signal Shape",
                Condition(XRHandFingerID.Thumb, Target(XRFingerShapeType.FullCurl, 0f, 0.25f, 0.25f), Target(XRFingerShapeType.Spread, 1f, 0.45f, 0.3f)),
                Curl(XRHandFingerID.Index, 1f, 0.15f, 0.15f),
                Curl(XRHandFingerID.Middle, 1f, 0.15f, 0.15f),
                Curl(XRHandFingerID.Ring, 1f, 0.15f, 0.15f),
                Curl(XRHandFingerID.Little, 1f, 0.15f, 0.15f));

            var pinch = CreateShape(folder, "Pinch Shape",
                Condition(XRHandFingerID.Index, Target(XRFingerShapeType.Pinch, 1f, 0.25f, 0f)));

            var thumbsUp = CreatePose(folder, "Thumbs Up Pose", thumb, XRHandAlignmentCondition.AlignsWith);
            var thumbsDown = CreatePose(folder, "Thumbs Down Pose", thumb, XRHandAlignmentCondition.OppositeTo);

            // Order matters: the first match wins, so more specific gestures go first.
            return new List<HandPoseDetector.PoseEntry>
            {
                Entry("Thumbs Up", thumbsUp, null, new Color(0.3f, 0.9f, 0.4f)),
                Entry("Thumbs Down", thumbsDown, null, new Color(0.95f, 0.35f, 0.3f)),
                Entry("Thumb Out", null, thumb, new Color(0.6f, 0.85f, 0.5f)),
                Entry("Fist", null, fist, new Color(0.9f, 0.3f, 0.25f)),
                Entry("Point", null, point, new Color(0.3f, 0.65f, 1f)),
                Entry("Shaka", null, shaka, new Color(1f, 0.6f, 0.2f)),
                Entry("Pinch", null, pinch, new Color(0.75f, 0.45f, 1f)),
                Entry("Grab", null, grab, new Color(1f, 0.85f, 0.25f)),
                Entry("Open Palm", null, open, new Color(0.3f, 0.95f, 0.95f)),
            };
        }

        static HandPoseDetector.PoseEntry Entry(string name, XRHandPose pose, XRHandShape shape, Color color) =>
            new() { displayName = name, handPose = pose, handShape = shape, highlight = color };

        static XRFingerShapeCondition.Target Target(XRFingerShapeType type, float desired, float lower, float upper) =>
            new() { shapeType = type, desired = desired, lowerTolerance = lower, upperTolerance = upper };

        static XRFingerShapeCondition Condition(XRHandFingerID finger, params XRFingerShapeCondition.Target[] targets) =>
            new() { fingerID = finger, targets = targets };

        static XRFingerShapeCondition Curl(XRHandFingerID finger, float desired, float lower, float upper) =>
            Condition(finger, Target(XRFingerShapeType.FullCurl, desired, lower, upper));

        static XRHandShape CreateShape(string folder, string name, params XRFingerShapeCondition[] conditions)
        {
            var shape = ScriptableObject.CreateInstance<XRHandShape>();
            shape.fingerShapeConditions = conditions.ToList();
            return CreateOrReplaceAsset(shape, $"{folder}/{name}.asset");
        }

        static XRHandPose CreatePose(string folder, string name, XRHandShape shape, XRHandAlignmentCondition thumbToUp)
        {
            var pose = ScriptableObject.CreateInstance<XRHandPose>();
            pose.handShape = shape;
            pose.relativeOrientation = new XRHandRelativeOrientation
            {
                userConditions = new[]
                {
                    new XRHandRelativeOrientation.UserCondition
                    {
                        handAxis = XRHandAxis.ThumbExtendedDirection,
                        alignmentCondition = thumbToUp,
                        referenceDirection = XRHandUserRelativeDirection.OriginUp,
                        angleTolerance = 60f,
                    },
                },
                targetConditions = new XRHandRelativeOrientation.TargetCondition[0],
            };
            return CreateOrReplaceAsset(pose, $"{folder}/{name}.asset");
        }

        // ---------------------------------------------------------------- Sign

        static void CreateInstructionSign()
        {
            var sign = new GameObject("Controls Sign");
            sign.transform.position = new Vector3(0f, 0f, 3.2f);

            var boardMat = SaveMaterial(StandardMaterial(new Color(0.18f, 0.13f, 0.1f), 0.1f), "Sign Board");

            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = "Board";
            board.transform.SetParent(sign.transform, false);
            board.transform.localPosition = new Vector3(0f, 1.75f, 0f);
            board.transform.localScale = new Vector3(2.3f, 1.25f, 0.05f);
            board.GetComponent<MeshRenderer>().sharedMaterial = boardMat;

            foreach (var x in new[] { -1f, 1f })
            {
                var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                post.name = "Post";
                post.transform.SetParent(sign.transform, false);
                post.transform.localPosition = new Vector3(x, 0.6f, 0.04f);
                post.transform.localScale = new Vector3(0.08f, 0.6f, 0.08f);
                post.GetComponent<MeshRenderer>().sharedMaterial = boardMat;
            }

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(sign.transform, false);
            textGo.transform.localPosition = new Vector3(0f, 1.75f, -0.03f);
            // TextMesh faces -Z; rotate so it reads toward the origin.
            textGo.transform.localRotation = Quaternion.identity;
            var text = CreateTextMesh(textGo,
                "HAND SIMULATOR\n" +
                "Right mouse drag  look     WASD / Q E  move\n" +
                "Tab  move body / hands     ] right hand   [ left hand\n" +
                "Gestures toggle on the right hand (hold Shift for left):\n" +
                "N  Point      M  Pinch      K  Fist      -  Rest\n" +
                "(first tap of a new gesture loads it, tap again to play)\n" +
                "CLAP: swing both open palms together", 0.012f, 48);
            text.color = new Color(1f, 0.93f, 0.8f);
        }

        static TextMesh CreateTextMesh(GameObject go, string content, float characterSize, int fontSize)
        {
            var text = go.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.font = font;
            text.text = content;
            text.fontSize = fontSize;
            text.characterSize = characterSize;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            return text;
        }

        // ---------------------------------------------------------------- Asset helpers

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static T CreateOrReplaceAsset<T>(T asset, string path) where T : Object
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(asset, path);
                return asset;
            }

            // Keep the GUID stable so references in other scenes survive a rebuild.
            EditorUtility.CopySerialized(asset, existing);
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(asset);
            return existing;
        }

        static Material StandardMaterial(Color color, float smoothness)
        {
            var material = new Material(Shader.Find("Standard")) { color = color };
            material.SetFloat("_Glossiness", smoothness);
            return material;
        }

        static Material SaveMaterial(Material material, string name)
        {
            material.name = name;
            return CreateOrReplaceAsset(material, $"{k_Root}/Materials/{name}.mat");
        }

        static Texture2D SaveTexture(Texture2D texture, string fileName, bool cutout)
        {
            texture.Apply();
            var path = $"{k_Root}/Textures/{fileName}";
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.wrapMode = cutout ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            importer.alphaIsTransparency = cutout;
            importer.anisoLevel = cutout ? 1 : 8;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static GameObject SavePrefab(GameObject instance, string name)
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, $"{k_Root}/Prefabs/{name}.prefab");
            Object.DestroyImmediate(instance);
            return prefab;
        }

        /// <summary>The first version of this scene was a desert; remove what it generated.</summary>
        static void DeleteObsoleteDesertAssets()
        {
            string[] obsolete =
            {
                k_Root + "/Scenes/DesertHandSim.unity",
                k_Root + "/Scenes/SavannaHandSim.unity",
                k_Root + "/Terrain/Desert.asset",
                k_Root + "/Terrain/Sand.terrainlayer",
                k_Root + "/Textures/Sand_Albedo.png",
                k_Root + "/Textures/Sand_Ripples_Normal.png",
                k_Root + "/Materials/Desert Rock.mat",
                k_Root + "/Materials/Desert Sky.mat",
            };
            foreach (var path in obsolete)
            {
                if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                    AssetDatabase.DeleteAsset(path);
            }
        }

        static void AddSceneToBuildSettings(string path)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != path && File.Exists(s.path)).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
