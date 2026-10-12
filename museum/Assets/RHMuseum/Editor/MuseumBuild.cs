using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RHMuseum.EditorTools
{
    /// <summary>
    /// Command-line Quest build, so the museum APK can be rebuilt without opening the editor:
    /// Unity -batchmode -quit -projectPath museum -buildTarget Android
    ///       -executeMethod RHMuseum.EditorTools.MuseumBuild.BuildAndroid -rhOutput path\to\museum.apk
    /// </summary>
    public static class MuseumBuild
    {
        const string ScenePath = "Assets/RHMuseum/Scenes/Museum.unity";

        public static void BuildAndroid()
        {
            string output = Arg("-rhOutput") ?? "Builds/museum.apk";
            MuseumSetup.RefreshMuseumJson();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            var s = report.summary;
            Debug.Log($"[RHMuseum] build {s.result}: {output} ({s.totalSize / (1024 * 1024)} MB, {s.totalErrors} errors)");
            // -quit alone exits 0 even when the build failed.
            if (s.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
