#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;

namespace RHMuseum.EditorTools
{
    /// <summary>
    /// Adds what the install/zone system needs to the generated Android manifest:
    ///   REQUEST_INSTALL_PACKAGES / REQUEST_DELETE_PACKAGES : standalone install mode (PackageInstaller)
    ///   QUERY_ALL_PACKAGES                                 : see which ported apps are installed (Android 11+ visibility)
    /// The museum is sideloaded, never Store-distributed, so QUERY_ALL_PACKAGES is acceptable here.
    /// Patching the generated file avoids maintaining a full custom main manifest per Unity version.
    /// </summary>
    public class AndroidManifestPatch : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 100;

        const string AndroidNs = "http://schemas.android.com/apk/res/android";
        static readonly string[] Permissions =
        {
            "android.permission.INTERNET",
            "android.permission.REQUEST_INSTALL_PACKAGES",
            "android.permission.REQUEST_DELETE_PACKAGES",
            "android.permission.QUERY_ALL_PACKAGES",
        };

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            string path = Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(path)) return;
            var doc = new XmlDocument();
            doc.Load(path);
            var manifest = doc.DocumentElement;
            foreach (var perm in Permissions)
            {
                bool present = false;
                foreach (XmlNode n in manifest.SelectNodes("uses-permission"))
                    if (n.Attributes?["android:name"]?.Value == perm) present = true;
                if (present) continue;
                var el = doc.CreateElement("uses-permission");
                el.SetAttribute("name", AndroidNs, perm);
                manifest.PrependChild(el);
            }
            doc.Save(path);
        }
    }
}
#endif
