using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace RHMuseum
{
    // Mirrors data/museum.json (written by pipeline/rhm/layout.py). JsonUtility ignores unknown fields,
    // so the pipeline can add fields without breaking older builds.

    [Serializable]
    public class MuseumDoc
    {
        public int schema_version;
        public string generated_at;
        public int max_per_exhibit;
        public List<Wing> wings = new List<Wing>();
        public List<ProjectInfo> projects = new List<ProjectInfo>();

        [NonSerialized] Dictionary<string, ProjectInfo> _byId;

        public ProjectInfo Project(string id)
        {
            if (_byId == null)
            {
                _byId = new Dictionary<string, ProjectInfo>();
                foreach (var p in projects) _byId[p.id] = p;
            }
            return _byId.TryGetValue(id, out var info) ? info : null;
        }
    }

    [Serializable]
    public class Wing
    {
        public int year;
        public string title;
        public int project_count;
        public List<Exhibit> exhibits = new List<Exhibit>();
    }

    [Serializable]
    public class Exhibit
    {
        public string id;
        public string kind;      // "device" | "archive"
        public string device;
        public string title;
        public string subtitle;
        public List<string> project_ids = new List<string>();

        public bool IsArchive => kind == "archive";
    }

    /// <summary>A smoke-tested Quest port (pipeline: data/port_status.json -> museum.json "app").</summary>
    [Serializable]
    public class AppInfo
    {
        public string package;
        public string apk_url;
        public string sha256;
        public long bytes;
        public string recipe;
        public bool tested;
    }

    [Serializable]
    public class LaunchTarget
    {
        public string kind;      // "theater" | "browser" | "horizon"
        public string url;
    }

    [Serializable]
    public class ProjectInfo
    {
        public string id;
        public int year;
        public string exhibit;
        public string title;
        public string tagline;
        public string synopsis;
        public string platform;
        public string device_label;
        public string requirement_tier;
        public string fidelity;  // Native | Ported | Ported-reduced | Simulated | Browser | Watch
        public List<string> extra_hardware = new List<string>();
        public bool winner;
        public List<string> prizes = new List<string>();
        public string thumbnail;
        public string video_url;
        public string devpost_url;
        public string repo_url;
        public string license_gate;
        public LaunchTarget launch = new LaunchTarget();
        public AppInfo app;          // null / empty package when there is no playable port yet
        public int rating_count;     // snapshot at layout time; RatingsClient has the live numbers
        public float avg_stars;
    }

    /// <summary>
    /// Loads museum.json: remote URL (so a new year needs no rebuild) -> last cached copy -> StreamingAssets.
    /// </summary>
    public static class MuseumDataLoader
    {
        const string FileName = "museum.json";

        public static IEnumerator Load(string remoteUrl, Action<MuseumDoc, string> done)
        {
            string cachePath = Path.Combine(Application.persistentDataPath, FileName);

            if (!string.IsNullOrEmpty(remoteUrl))
            {
                using (var req = UnityWebRequest.Get(remoteUrl))
                {
                    req.timeout = 6;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success && TryParse(req.downloadHandler.text, out var doc))
                    {
                        try { File.WriteAllText(cachePath, req.downloadHandler.text); }
                        catch (Exception e) { Debug.LogWarning($"[RHMuseum] cache write failed: {e.Message}"); }
                        done(doc, "remote");
                        yield break;
                    }
                    Debug.LogWarning($"[RHMuseum] remote museum.json unavailable ({req.error}); falling back");
                }
            }

            if (File.Exists(cachePath) && TryParse(File.ReadAllText(cachePath), out var cached))
            {
                done(cached, "cache");
                yield break;
            }

            // StreamingAssets is inside the APK on Android, so it has to go through UnityWebRequest.
            string bundled = Path.Combine(Application.streamingAssetsPath, FileName);
            if (!bundled.Contains("://")) bundled = "file://" + bundled;
            using (var req = UnityWebRequest.Get(bundled))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success && TryParse(req.downloadHandler.text, out var doc))
                {
                    done(doc, "bundled");
                    yield break;
                }
                Debug.LogError($"[RHMuseum] no museum.json found ({req.error}). Run 'RH Museum/Setup Project' in the editor.");
                done(null, "none");
            }
        }

        public static bool TryParse(string json, out MuseumDoc doc)
        {
            doc = null;
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                doc = JsonUtility.FromJson<MuseumDoc>(json);
                return doc != null && doc.wings != null && doc.wings.Count > 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RHMuseum] museum.json parse failed: {e.Message}");
                return false;
            }
        }
    }
}
