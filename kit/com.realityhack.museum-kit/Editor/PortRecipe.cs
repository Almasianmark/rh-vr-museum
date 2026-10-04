using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RealityHack.MuseumKit.Editor
{
    /// <summary>
    /// Batch-mode port recipe, run by the factory inside a GameCI container:
    ///
    ///   unity-editor -batchmode -quit -projectPath . -buildTarget Android \
    ///     -executeMethod RealityHack.MuseumKit.Editor.PortRecipe.Run \
    ///     -rhOutput /out/app.apk -rhPackageId world.realityhack.p2023.failtopia -rhProductName "Failtopia" -rhRecipe native
    ///
    /// Steps: Quest Android player settings → XR loader for Android (OpenXR + Meta Quest feature, or the
    /// Oculus loader for 2019 projects) → build the project's own scene list → write build-report.json.
    /// Exit code 0 = APK built; anything else lands the project in the triage queue.
    /// C# 7.3 so it compiles in Unity 2019 projects.
    /// </summary>
    public static class PortRecipe
    {
        public static void Run()
        {
            string output = Arg("-rhOutput") ?? "Builds/port.apk";
            string reportPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".", "build-report.json");
            var report = new Report { recipe = Arg("-rhRecipe") ?? "native", unity = Application.unityVersion };
            int code = 1;
            try
            {
                ConfigurePlayer(Arg("-rhPackageId"), Arg("-rhProductName"));
                report.xr = ConfigureXr(report.recipe);
                code = Build(output, report);
            }
            catch (Exception e)
            {
                report.error = e.GetType().Name + ": " + e.Message;
                Debug.LogError("[RHKit] port recipe failed: " + e);
            }
            report.exitCode = code;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");
            File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
            EditorApplication.Exit(code);
        }

        [Serializable]
        class Report
        {
            public string recipe, unity, xr, result, error;
            public string[] scenes;
            public long apkBytes;
            public int errors, warnings, exitCode;
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // ------------------------------------------------------------------ player settings

        static void ConfigurePlayer(string packageId, string productName)
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            if (!string.IsNullOrEmpty(packageId)) PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, packageId);
            if (!string.IsNullOrEmpty(productName)) PlayerSettings.productName = productName;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)29;   // Quest 2 baseline for sideloading
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetMobileMTRendering(BuildTargetGroup.Android, true);
            // Quest refuses to show VR apps launched from 2D unless they're declared VR; XR loaders add the
            // manifest bits, so nothing else is needed here.
        }

        // ------------------------------------------------------------------ XR

        /// <summary>
        /// Enables an Android XR loader through XR Plug-in Management (reflection, so this compiles even if
        /// the project doesn't have the package yet; the factory adds it to Packages/manifest.json).
        /// </summary>
        static string ConfigureXr(string recipe)
        {
            var perTarget = FindType("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget, Unity.XR.Management.Editor");
            var store = FindType("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore, Unity.XR.Management.Editor");
            if (perTarget == null || store == null) return "xr-management missing (project left as is)";

            // GetOrCreate() (internal) -> instance; create Android manager settings if missing (verified
            // against com.unity.xr.management 4.4.0 source).
            var getOrCreate = perTarget.GetMethod("GetOrCreate", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            object instance = getOrCreate != null ? getOrCreate.Invoke(null, null) : null;
            if (instance == null) return "could not create XR settings asset (needs manual setup)";
            var t = instance.GetType();
            var has = t.GetMethod("HasManagerSettingsForBuildTarget");
            if (has != null && !(bool)has.Invoke(instance, new object[] { BuildTargetGroup.Android }))
            {
                var createDefault = t.GetMethod("CreateDefaultManagerSettingsForBuildTarget");
                if (createDefault != null) createDefault.Invoke(instance, new object[] { BuildTargetGroup.Android });
            }
            var getManager = t.GetMethod("ManagerSettingsForBuildTarget");
            object manager = getManager != null ? getManager.Invoke(instance, new object[] { BuildTargetGroup.Android }) : null;
            if (manager == null) return "no XRManagerSettings for Android (needs manual setup)";

            // Already has an Android loader (e.g. Quest-native projects using Oculus XR / Meta SDK): keep it.
            var active = manager.GetType().GetProperty("activeLoaders");
            var loaders = active != null ? active.GetValue(manager, null) as System.Collections.ICollection : null;
            if (loaders != null && loaders.Count > 0) return "kept existing Android loader(s): " + loaders.Count;

            bool hasOpenXr = FindType("UnityEngine.XR.OpenXR.OpenXRLoader, Unity.XR.OpenXR") != null;
            bool hasOculus = FindType("Unity.XR.Oculus.OculusLoader, Unity.XR.Oculus") != null;
            string loader = hasOpenXr ? "UnityEngine.XR.OpenXR.OpenXRLoader" : hasOculus ? "Unity.XR.Oculus.OculusLoader" : null;
            if (loader == null) return "no Quest-capable XR loader package";

            var assign = store.GetMethod("AssignLoader", BindingFlags.Public | BindingFlags.Static);
            bool ok = assign != null && (bool)assign.Invoke(null, new object[] { manager, loader, BuildTargetGroup.Android });
            string result = (ok ? "assigned " : "already/failed ") + loader;

            if (hasOpenXr)
            {
                EnableOpenXrFeature("com.unity.openxr.feature.metaquest");
                EnableOpenXrFeature("com.unity.openxr.feature.input.oculustouch");
                if (recipe == "passthrough")
                    foreach (var f in new[] { "session", "camera", "plane", "raycast" })   // ids from com.unity.xr.meta-openxr
                        EnableOpenXrFeature("com.unity.openxr.feature.arfoundation-meta-" + f);
            }
            AssetDatabase.SaveAssets();
            return result;
        }

        static void EnableOpenXrFeature(string id)
        {
            var helpers = FindType("UnityEditor.XR.OpenXR.Features.FeatureHelpers, Unity.XR.OpenXR.Editor");
            var get = helpers != null ? helpers.GetMethod("GetFeatureWithIdForBuildTarget", BindingFlags.Public | BindingFlags.Static) : null;
            object feature = get != null ? get.Invoke(null, new object[] { BuildTargetGroup.Android, id }) : null;
            var enabled = feature != null ? feature.GetType().GetProperty("enabled") : null;
            if (enabled != null) enabled.SetValue(feature, true, null);
            else Debug.LogWarning("[RHKit] OpenXR feature not found: " + id);
        }

        static Type FindType(string assemblyQualified)
        {
            var t = Type.GetType(assemblyQualified, false);
            if (t != null) return t;
            string name = assemblyQualified.Split(',')[0];
            return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(x => x != null);
        }

        // ------------------------------------------------------------------ build

        static int Build(string output, Report report)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
            {
                // Hackathon projects often never set a scene list; take every scene, Assets-first.
                scenes = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath)
                    .OrderBy(p => p.Count(c => c == '/')).ToArray();
            }
            report.scenes = scenes;
            if (scenes.Length == 0)
            {
                report.result = "NoScenes";
                return 2;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };
            BuildReport r = BuildPipeline.BuildPlayer(options);
            report.result = r.summary.result.ToString();
            report.errors = r.summary.totalErrors;
            report.warnings = r.summary.totalWarnings;
            report.apkBytes = File.Exists(output) ? new FileInfo(output).Length : 0;
            return r.summary.result == BuildResult.Succeeded ? 0 : 3;
        }
    }
}
