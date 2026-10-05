using System;
using System.Collections.Generic;
using UnityEngine;

namespace RHMuseum.Apps
{
    /// <summary>
    /// Thin wrapper over Assets/Plugins/Android/RHInstaller.java. In the editor / on desktop it simulates
    /// installs (the result arrives a second later) so the whole zone flow can be tested without a headset.
    /// </summary>
    public static class AndroidApps
    {
        public const string CallbackObject = "RHAppManager";

#if UNITY_ANDROID && !UNITY_EDITOR
        const string Plugin = "world.realityhack.museum.RHInstaller";

        static AndroidJavaObject Activity()
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                return player.GetStatic<AndroidJavaObject>("currentActivity");
        }

        static T Call<T>(string method, params object[] args)
        {
            using (var cls = new AndroidJavaClass(Plugin)) return cls.CallStatic<T>(method, args);
        }

        public static void Init()
        {
            using (var a = Activity())
            using (var cls = new AndroidJavaClass(Plugin)) cls.CallStatic("init", a, CallbackObject);
        }

        public static bool CanInstall() { using (var a = Activity()) return Call<bool>("canInstall", a); }
        public static void OpenInstallPermissionSettings()
        {
            using (var a = Activity())
            using (var cls = new AndroidJavaClass(Plugin)) cls.CallStatic("openInstallPermissionSettings", a);
        }
        public static string Install(string apkPath, string package) { using (var a = Activity()) return Call<string>("install", a, apkPath, package); }
        public static string Uninstall(string package) { using (var a = Activity()) return Call<string>("uninstall", a, package); }
        public static bool IsInstalled(string package) { using (var a = Activity()) return Call<long>("installedAt", a, package) >= 0; }
        public static bool Launch(string package, string projectId) { using (var a = Activity()) return Call<bool>("launch", a, package, projectId); }
        public static long FreeBytes(string path) => Call<long>("freeBytes", path);
        public static bool Simulated => false;
#else
        // ---- desktop / editor simulation ----
        static readonly HashSet<string> _installed = new HashSet<string>();
        public static void Init() { }
        public static bool CanInstall() => true;
        public static void OpenInstallPermissionSettings() => Debug.Log("[RHMuseum] (sim) open 'Install unknown apps' settings");
        public static string Install(string apkPath, string package)
        {
            Simulate("install", package, () => _installed.Add(package));
            return "";
        }
        public static string Uninstall(string package)
        {
            Simulate("uninstall", package, () => _installed.Remove(package));
            return "";
        }
        public static bool IsInstalled(string package) => _installed.Contains(package);
        public static bool Launch(string package, string projectId)
        {
            Debug.Log($"[RHMuseum] (sim) launch {package} for {projectId}");
            return _installed.Contains(package);
        }
        public static long FreeBytes(string path) => 64L << 30;
        public static bool Simulated => true;

        static void Simulate(string op, string package, Action apply)
        {
            var go = GameObject.Find(CallbackObject);
            if (go == null) return;
            go.GetComponent<MonoBehaviour>().StartCoroutine(Delayed());
            System.Collections.IEnumerator Delayed()
            {
                yield return new WaitForSeconds(1f);
                apply();
                go.SendMessage("OnPackageResult", $"{op}|{package}|0|simulated");
            }
        }
#endif
    }
}
