using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace RHMuseum.EditorTools
{
    /// <summary>
    /// One-click setup for a freshly cloned project: URP asset, materials, TMP essentials,
    /// museum.json in StreamingAssets, the Museum scene, and Quest Android player settings.
    /// XR Plug-in Management (OpenXR + Meta Quest feature) is the only manual step. See museum/README.md.
    /// </summary>
    public static class MuseumSetup
    {
        const string Root = "Assets/RHMuseum";
        const string ResourcesDir = Root + "/Resources";
        const string SettingsDir = Root + "/Settings";
        const string ScenePath = Root + "/Scenes/Museum.unity";

        [MenuItem("RH Museum/Setup Project", priority = 0)]
        public static void SetupProject()
        {
            EnsureUrp();
            CreateMaterials();
            ImportTmpEssentials();
            RefreshMuseumJson();
            ConfigureAndroid();
            CreateScene();
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("RH Museum",
                "Setup done.\n\nLast manual step for Quest: Project Settings > XR Plug-in Management > Android tab > " +
                "tick OpenXR, then under OpenXR enable the 'Meta Quest Support' feature and add the 'Oculus Touch Controller Profile'.\n\n" +
                "Press Play to walk the museum on desktop (WASD, right-drag to look, click paintings).", "OK");
        }

        [MenuItem("RH Museum/Refresh museum.json", priority = 1)]
        public static void RefreshMuseumJson()
        {
            string src = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data", "museum.json"));
            if (!File.Exists(src))
            {
                Debug.LogError($"[RHMuseum] {src} not found. Run the pipeline: cd pipeline && python -m rhm.build");
                return;
            }
            string dstDir = Path.Combine(Application.dataPath, "StreamingAssets");
            Directory.CreateDirectory(dstDir);
            File.Copy(src, Path.Combine(dstDir, "museum.json"), true);
            AssetDatabase.Refresh();
            Debug.Log($"[RHMuseum] copied {src} -> Assets/StreamingAssets/museum.json");
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static void EnsureUrp()
        {
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset) return;
            EnsureFolder(SettingsDir);
            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, SettingsDir + "/QuestRenderer.asset");
            var asset = UniversalRenderPipelineAsset.Create(rendererData);
            // Quest 2 budget: no HDR, no shadows (greybox uses baked-in fake lighting), 4x MSAA.
            asset.supportsHDR = false;
            asset.shadowDistance = 0;
            asset.msaaSampleCount = 4;
            AssetDatabase.CreateAsset(asset, SettingsDir + "/QuestURP.asset");
            GraphicsSettings.defaultRenderPipeline = asset;
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = asset;
            }
            Debug.Log("[RHMuseum] created and assigned URP asset " + SettingsDir + "/QuestURP.asset");
        }

        static void CreateMaterials()
        {
            EnsureFolder(ResourcesDir);
            Make("RHM_Greybox", "RHMuseum/Greybox");
            Make("RHM_Ripple", "RHMuseum/PortalRipple");
            Make("RHM_Fade", "RHMuseum/Fade");
        }

        static void Make(string name, string shaderName)
        {
            string path = $"{ResourcesDir}/{name}.mat";
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError($"[RHMuseum] shader {shaderName} not found (compile errors?)");
                return;
            }
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                existing.shader = shader;
                return;
            }
            AssetDatabase.CreateAsset(new Material(shader) { enableInstancing = true }, path);
        }

        static void ImportTmpEssentials()
        {
            // TMP_PackageResourceImporter lives in the TextMeshPro editor assembly; call via reflection so a
            // renamed API produces a log line, not a compile error.
            if (AssetDatabase.FindAssets("t:TMP_Settings").Length > 0) return;
            var type = Type.GetType("TMPro.TMP_PackageResourceImporter, Unity.TextMeshPro.Editor");
            var method = type?.GetMethod("ImportResources", new[] { typeof(bool), typeof(bool), typeof(bool) });
            if (method != null) method.Invoke(null, new object[] { true, false, false });
            else Debug.LogWarning("[RHMuseum] Import TMP Essentials manually: Window > TextMeshPro > Import TMP Essential Resources");
        }

        static void ConfigureAndroid()
        {
            var android = NamedBuildTarget.Android;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;   // Quest OS baseline
            PlayerSettings.SetApplicationIdentifier(android, "world.realityhack.museum");
            PlayerSettings.productName = "Reality Hack Museum";
            PlayerSettings.companyName = "Reality Hack";
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
        }

        static void CreateScene()
        {
            EnsureFolder(Root + "/Scenes");
            if (!File.Exists(ScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("Museum Bootstrap").AddComponent<MuseumBootstrap>();
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.6f);
                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            else EditorSceneManager.OpenScene(ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }
    }
}
