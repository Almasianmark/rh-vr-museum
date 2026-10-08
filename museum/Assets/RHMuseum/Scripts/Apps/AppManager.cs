using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace RHMuseum.Apps
{
    public enum InstallMode { Standalone, Companion }

    public enum AppState { NotDownloaded, Queued, Downloading, Verifying, Downloaded, Installing, Installed, UpdateAvailable, Failed }

    /// <summary>
    /// Install / zone system (build-order step 5).
    ///   Approach zone (spine near a wing's opening, or inside the wing)  -> background-download that wing's APKs, verify SHA-256
    ///   Entrance zone (first metres of the wing corridor)                 -> install everything downloaded, batched
    ///   Standalone mode : PackageInstaller (RHInstaller.java), one Android prompt per app
    ///   Companion mode  : write companion/requests.json; a PC / Raspberry Pi running companion/rh_companion.py installs
    ///                     silently over ADB Wi-Fi (`pm install -g`) and writes companion/status.json back
    ///   Storage budget  : before installing, uninstall least-recently-played first, weighted by rating (StoragePlanner)
    /// </summary>
    public class AppManager : MonoBehaviour
    {
        public static AppManager Instance { get; private set; }
        public static event Action<string> Changed;          // package id, or null for "everything / settings"

        public float approachRadius = 16f;
        public float entranceDepth = 4f;

        public class Entry
        {
            public ProjectInfo project;
            public AppInfo app;
            public AppState state;
            public float progress;
            public string error;
            public string ApkPath => Path.Combine(AppsDir, $"{app.package}-{app.sha256.Substring(0, Math.Min(12, app.sha256.Length))}.apk");
        }

        [Serializable] class Record { public string package; public string sha256; public long installedUtc; public long lastPlayedUtc; }
        [Serializable] class Records { public List<Record> items = new List<Record>(); }
        [Serializable] class CompanionApp { public string package; public string apk_url; public string sha256; public long bytes; public string project_id; }
        [Serializable] class CompanionRequest { public int seq; public string mode = "companion"; public List<CompanionApp> install = new List<CompanionApp>(); public List<string> uninstall = new List<string>(); }
        [Serializable] class CompanionStatus { public int seq_done; public List<string> installed = new List<string>(); public string message; public long updated; }

        public readonly Dictionary<string, Entry> Apps = new Dictionary<string, Entry>();
        public InstallMode Mode { get; private set; }
        public long BudgetBytes { get; private set; }
        public string Status { get; private set; } = "";

        static string AppsDir => Path.Combine(Application.persistentDataPath, "apks");
        static string CompanionDir => Path.Combine(Application.persistentDataPath, "companion");

        readonly Dictionary<string, Record> _records = new Dictionary<string, Record>();
        readonly Queue<Entry> _downloadQueue = new Queue<Entry>();
        readonly HashSet<MuseumBuilder.WingInfo> _wingsInEntrance = new HashSet<MuseumBuilder.WingInfo>();
        List<MuseumBuilder.WingInfo> _wings;
        Dictionary<MuseumBuilder.WingInfo, List<Entry>> _wingApps;
        Transform _head;
        bool _downloading, _batchRunning;
        string _awaitPackage;
        int _awaitStatus;
        int _companionSeq;

        const string PrefMode = "rhm.apps.mode", PrefBudget = "rhm.apps.budget_gb", PrefRecords = "rhm.apps.records";

        // ------------------------------------------------------------------ setup

        public static AppManager Create(MuseumDoc doc, List<MuseumBuilder.WingInfo> wings, Transform head)
        {
            // Name must match AndroidApps.CallbackObject: Java calls UnitySendMessage on it.
            var go = new GameObject(AndroidApps.CallbackObject);
            var m = go.AddComponent<AppManager>();
            Instance = m;
            m._wings = wings;
            m._head = head;
            m.Load(doc);
            return m;
        }

        void Load(MuseumDoc doc)
        {
            Directory.CreateDirectory(AppsDir);
            Directory.CreateDirectory(CompanionDir);
            Mode = (InstallMode)PlayerPrefs.GetInt(PrefMode, (int)InstallMode.Standalone);
            BudgetBytes = (long)(PlayerPrefs.GetFloat(PrefBudget, 8f) * (1L << 30));
            var saved = JsonUtility.FromJson<Records>(PlayerPrefs.GetString(PrefRecords, "{}")) ?? new Records();
            foreach (var r in saved.items) _records[r.package] = r;

            foreach (var p in doc.projects)
                if (p.app != null && !string.IsNullOrEmpty(p.app.package) && !string.IsNullOrEmpty(p.app.sha256))
                    Apps[p.app.package] = new Entry { project = p, app = p.app };

            _wingApps = _wings.ToDictionary(w => w, w => w.paintings
                .Where(v => v.Info.app != null && Apps.ContainsKey(v.Info.app.package ?? ""))
                .Select(v => Apps[v.Info.app.package]).ToList());

            AndroidApps.Init();
            RefreshInstalled();
        }

        void Start()
        {
            StartCoroutine(ZoneLoop());
            StartCoroutine(CompanionLoop());
        }

        // ------------------------------------------------------------------ settings (lobby panel)

        public void SetMode(InstallMode mode)
        {
            Mode = mode;
            PlayerPrefs.SetInt(PrefMode, (int)mode);
            PlayerPrefs.Save();
            Changed?.Invoke(null);
        }

        public void SetBudgetGb(float gb)
        {
            gb = Mathf.Clamp(gb, 1f, 128f);
            BudgetBytes = (long)(gb * (1L << 30));
            PlayerPrefs.SetFloat(PrefBudget, gb);
            PlayerPrefs.Save();
            Changed?.Invoke(null);
        }

        public long UsedBytes => Apps.Values.Where(e => e.state == AppState.Installed || e.state == AppState.UpdateAvailable)
                                     .Sum(e => StoragePlanner.Footprint(e.app.bytes))
                                 + Apps.Values.Where(e => File.Exists(e.ApkPath)).Sum(e => e.app.bytes);

        // ------------------------------------------------------------------ state

        public Entry For(ProjectInfo p) =>
            p?.app != null && !string.IsNullOrEmpty(p.app.package) && Apps.TryGetValue(p.app.package, out var e) ? e : null;

        void RefreshInstalled()
        {
            foreach (var e in Apps.Values)
            {
                if (e.state == AppState.Downloading || e.state == AppState.Installing || e.state == AppState.Verifying) continue;
                bool installed = AndroidApps.IsInstalled(e.app.package);
                if (installed)
                {
                    if (!_records.ContainsKey(e.app.package))   // installed outside the museum (companion / preload)
                        _records[e.app.package] = new Record { package = e.app.package, sha256 = e.app.sha256, installedUtc = Now(), lastPlayedUtc = Now() };
                    e.state = _records[e.app.package].sha256 == e.app.sha256 ? AppState.Installed : AppState.UpdateAvailable;
                }
                else
                {
                    _records.Remove(e.app.package);
                    e.state = File.Exists(e.ApkPath) ? AppState.Downloaded : (e.state == AppState.Queued ? AppState.Queued : AppState.NotDownloaded);
                }
            }
            SaveRecords();
            Changed?.Invoke(null);
        }

        void SaveRecords()
        {
            PlayerPrefs.SetString(PrefRecords, JsonUtility.ToJson(new Records { items = _records.Values.ToList() }));
            PlayerPrefs.Save();
        }

        static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        void OnApplicationFocus(bool focused)
        {
            if (focused) RefreshInstalled();   // back from an app, an install prompt, or Settings
        }

        // ------------------------------------------------------------------ zones

        IEnumerator ZoneLoop()
        {
            var wait = new WaitForSeconds(0.5f);
            while (true)
            {
                if (_head != null)
                    foreach (var wing in _wings)
                    {
                        var apps = _wingApps[wing];
                        if (apps.Count == 0) continue;
                        Vector3 local = wing.root.transform.InverseTransformPoint(_head.position);
                        bool approach = Vector3.Distance(_head.position, wing.root.transform.position) < approachRadius
                                        || wing.bounds.Contains(_head.position);
                        bool entrance = local.x > 0.2f && local.x < entranceDepth && Mathf.Abs(local.z) < MuseumBuilder.CorridorW / 2;

                        if (approach && Mode == InstallMode.Standalone) QueueDownloads(apps);
                        if (entrance && !_wingsInEntrance.Contains(wing))
                        {
                            _wingsInEntrance.Add(wing);
                            if (!_batchRunning) StartCoroutine(InstallBatch(wing.data.title, apps));
                        }
                        else if (!entrance) _wingsInEntrance.Remove(wing);
                    }
                yield return wait;
            }
        }

        public string WingSummary(MuseumBuilder.WingInfo wing)
        {
            var apps = _wingApps != null && _wingApps.TryGetValue(wing, out var a) ? a : null;
            if (apps == null || apps.Count == 0) return "No playable ports in this wing yet: every painting has its demo video.";
            int installed = apps.Count(e => e.state == AppState.Installed);
            int ready = apps.Count(e => e.state == AppState.Downloaded);
            var dl = apps.FirstOrDefault(e => e.state == AppState.Downloading);
            string mode = Mode == InstallMode.Companion ? "companion installs over Wi-Fi" : "installs at this entrance";
            return $"<b>{apps.Count} playable</b> · {installed} installed · {ready} ready to install" +
                   (dl != null ? $" · downloading {dl.project.title} {dl.progress:P0}" : "") + $"\n<size=70%>{mode}</size>";
        }

        // ------------------------------------------------------------------ downloads

        public void QueueDownloads(IEnumerable<Entry> entries)
        {
            foreach (var e in entries)
                if (e.state == AppState.NotDownloaded || e.state == AppState.UpdateAvailable && !File.Exists(e.ApkPath))
                {
                    if (e.state == AppState.NotDownloaded) e.state = AppState.Queued;
                    if (!_downloadQueue.Contains(e)) _downloadQueue.Enqueue(e);
                }
            if (!_downloading && _downloadQueue.Count > 0) StartCoroutine(DownloadLoop());
        }

        IEnumerator DownloadLoop()
        {
            _downloading = true;
            while (_downloadQueue.Count > 0)
            {
                var e = _downloadQueue.Dequeue();
                if (File.Exists(e.ApkPath)) { e.state = e.state == AppState.UpdateAvailable ? e.state : AppState.Downloaded; continue; }
                long free = AndroidApps.FreeBytes(Application.persistentDataPath);
                if (!StoragePlanner.CanDownload(e.app.bytes, free))
                {
                    Fail(e, "not enough free space on the headset");
                    continue;
                }
                yield return Download(e);
            }
            _downloading = false;
        }

        IEnumerator Download(Entry e)
        {
            bool wasUpdate = e.state == AppState.UpdateAvailable;
            e.state = AppState.Downloading;
            e.progress = 0;
            Changed?.Invoke(e.app.package);
            string tmp = e.ApkPath + ".part";
            using (var req = new UnityWebRequest(e.app.apk_url, "GET") { downloadHandler = new DownloadHandlerFile(tmp) { removeFileOnAbort = true } })
            {
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    e.progress = req.downloadProgress;
                    yield return new WaitForSeconds(0.25f);
                    Changed?.Invoke(e.app.package);
                }
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Fail(e, $"download failed: {req.error}");
                    yield break;
                }
            }

            e.state = AppState.Verifying;
            Changed?.Invoke(e.app.package);
            var hash = Task.Run(() => Sha256(tmp));
            while (!hash.IsCompleted) yield return null;
            if (hash.IsFaulted || !string.Equals(hash.Result, e.app.sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                Fail(e, "SHA-256 mismatch: download discarded");
                yield break;
            }
            if (File.Exists(e.ApkPath)) File.Delete(e.ApkPath);
            File.Move(tmp, e.ApkPath);
            e.state = wasUpdate ? AppState.UpdateAvailable : AppState.Downloaded;
            e.error = null;
            Changed?.Invoke(e.app.package);
        }

        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        void Fail(Entry e, string why)
        {
            e.state = AppState.Failed;
            e.error = why;
            Debug.LogWarning($"[RHMuseum] {e.app.package}: {why}");
            Changed?.Invoke(e.app.package);
        }

        // ------------------------------------------------------------------ installs

        IEnumerator InstallBatch(string label, List<Entry> wingApps)
        {
            _batchRunning = true;
            var toInstall = wingApps.Where(e => File.Exists(e.ApkPath) &&
                                                (e.state == AppState.Downloaded || e.state == AppState.UpdateAvailable)).ToList();
            if (Mode == InstallMode.Companion)
            {
                var wanted = wingApps.Where(e => e.state != AppState.Installed).ToList();
                WriteCompanionRequest(wanted, Evictions(wingApps, wanted.Sum(e => StoragePlanner.Footprint(e.app.bytes))) ?? new List<string>());
                Status = $"{label}: asked the companion to install {wingApps.Count(e => e.state != AppState.Installed)} app(s)";
                Changed?.Invoke(null);
                _batchRunning = false;
                yield break;
            }
            if (toInstall.Count == 0) { _batchRunning = false; yield break; }

            if (!AndroidApps.CanInstall())
            {
                Status = "Allow 'Install unknown apps' for the museum, then walk through the entrance again.";
                Changed?.Invoke(null);
                AndroidApps.OpenInstallPermissionSettings();
                _batchRunning = false;
                yield break;
            }

            long need = toInstall.Sum(e => StoragePlanner.Footprint(e.app.bytes));
            var evict = Evictions(toInstall, need);
            if (evict == null)
            {
                // Can't make room for all of them: install the batch in order while it fits.
                long room = BudgetBytes - UsedBytes;
                toInstall = toInstall.TakeWhile(e => (room -= StoragePlanner.Footprint(e.app.bytes)) >= 0).ToList();
                evict = new List<string>();
                Status = toInstall.Count == 0 ? "Storage budget full: raise it in the lobby, or uninstall from there." : Status;
            }

            foreach (var pkg in evict)
            {
                Status = $"Making room: uninstalling {Apps[pkg].project.title}";
                Changed?.Invoke(null);
                yield return RunPackageOp("uninstall", pkg, () => AndroidApps.Uninstall(pkg));
            }
            for (int i = 0; i < toInstall.Count; i++)
            {
                var e = toInstall[i];
                Status = $"{label}: installing {i + 1}/{toInstall.Count}: {e.project.title}";
                e.state = AppState.Installing;
                Changed?.Invoke(e.app.package);
                yield return RunPackageOp("install", e.app.package, () => AndroidApps.Install(e.ApkPath, e.app.package));
                if (_awaitStatus == 3) { Status = "Install cancelled; the rest of this batch was skipped."; break; }   // user said no
            }
            if (!Status.StartsWith("Install cancelled")) Status = "";
            _batchRunning = false;
            RefreshInstalled();
        }

        List<string> Evictions(IEnumerable<Entry> batch, long needBytes)
        {
            var protect = new HashSet<string>(batch.Select(e => e.app.package));
            var installed = Apps.Values.Where(e => e.state == AppState.Installed || e.state == AppState.UpdateAvailable).Select(e => new InstalledApp
            {
                package = e.app.package,
                bytes = StoragePlanner.Footprint(e.app.bytes),
                lastPlayedUtc = DateTimeOffset.FromUnixTimeSeconds(_records.TryGetValue(e.app.package, out var r) ? r.lastPlayedUtc : Now()).UtcDateTime,
                rating = Rating(e.project),
            });
            return StoragePlanner.PlanEvictions(installed, UsedBytes, needBytes, BudgetBytes, protect, DateTime.UtcNow);
        }

        static float Rating(ProjectInfo p)
        {
            var client = RatingsClient.Instance;
            if (client != null && client.TryGetScore(p.id, out var s) && s.bayes_score > 0) return s.bayes_score;
            return p.avg_stars > 0 ? p.avg_stars : 3f;
        }

        IEnumerator RunPackageOp(string op, string package, Func<string> start)
        {
            _awaitPackage = package;
            _awaitStatus = int.MinValue;
            string err = start();
            if (!string.IsNullOrEmpty(err))
            {
                if (Apps.TryGetValue(package, out var e)) Fail(e, $"{op} failed: {err}");
                yield break;
            }
            // -1 = waiting on the Android confirmation dialog; wait for the final status (or give up after 5 min).
            float deadline = Time.realtimeSinceStartup + 300f;
            while ((_awaitStatus == int.MinValue || _awaitStatus == -1) && Time.realtimeSinceStartup < deadline) yield return null;
        }

        /// <summary>Called by RHInstaller.java: "op|package|status|message".</summary>
        public void OnPackageResult(string msg)
        {
            var parts = msg.Split(new[] { '|' }, 4);
            if (parts.Length < 3) return;
            string op = parts[0], pkg = parts[1];
            int.TryParse(parts[2], out int status);
            if (pkg == _awaitPackage) _awaitStatus = status;
            if (!Apps.TryGetValue(pkg, out var e) || status == -1) return;

            if (status == 0 && op == "install")
            {
                _records[pkg] = new Record { package = pkg, sha256 = e.app.sha256, installedUtc = Now(), lastPlayedUtc = Now() };
                if (File.Exists(e.ApkPath)) File.Delete(e.ApkPath);     // installed: the APK is no longer needed
                e.state = AppState.Installed;
                e.error = null;
            }
            else if (status == 0 && op == "uninstall")
            {
                _records.Remove(pkg);
                e.state = File.Exists(e.ApkPath) ? AppState.Downloaded : AppState.NotDownloaded;
            }
            else if (op == "install")
            {
                e.state = File.Exists(e.ApkPath) ? AppState.Downloaded : AppState.NotDownloaded;
                e.error = status == 3 ? "install cancelled" : $"install failed ({status}) {(parts.Length > 3 ? parts[3] : "")}";
            }
            SaveRecords();
            Changed?.Invoke(pkg);
        }

        // ------------------------------------------------------------------ launch / manage

        public bool Launch(ProjectInfo p)
        {
            var e = For(p);
            if (e == null || (e.state != AppState.Installed && e.state != AppState.UpdateAvailable)) return false;
            if (!AndroidApps.Launch(e.app.package, p.id)) return false;
            if (_records.TryGetValue(e.app.package, out var r)) r.lastPlayedUtc = Now();
            SaveRecords();
            return true;
        }

        /// <summary>"Install now" from the theater: download (if needed) then install just this one.</summary>
        public void InstallNow(ProjectInfo p)
        {
            var e = For(p);
            if (e == null || _batchRunning) return;
            StartCoroutine(InstallNowRoutine(e));
        }

        IEnumerator InstallNowRoutine(Entry e)
        {
            if (!File.Exists(e.ApkPath) && Mode == InstallMode.Standalone)
            {
                QueueDownloads(new[] { e });
                while (e.state == AppState.Queued || e.state == AppState.Downloading || e.state == AppState.Verifying) yield return null;
            }
            yield return InstallBatch(e.project.title, new List<Entry> { e });
        }

        /// <summary>Preload: best-rated ports that fit the budget (setup time, before visitors arrive).</summary>
        public void Preload()
        {
            var pick = StoragePlanner.PreloadSelection(Apps.Values.Where(e => e.state != AppState.Installed),
                e => StoragePlanner.Footprint(e.app.bytes), e => Rating(e.project), UsedBytes, BudgetBytes);
            if (Mode == InstallMode.Companion)
            {
                WriteCompanionRequest(pick, new List<string>());
                Status = $"Preload: companion asked to install {pick.Count} app(s)";
            }
            else
            {
                QueueDownloads(pick);
                StartCoroutine(PreloadInstall(pick));
                Status = $"Preload: downloading {pick.Count} app(s); each install asks once";
            }
            Changed?.Invoke(null);
        }

        IEnumerator PreloadInstall(List<Entry> pick)
        {
            while (_downloading) yield return new WaitForSeconds(1f);
            while (_batchRunning) yield return null;
            yield return InstallBatch("Preload", pick);
        }

        // ------------------------------------------------------------------ companion bridge

        void WriteCompanionRequest(IEnumerable<Entry> install, List<string> uninstall)
        {
            var req = new CompanionRequest { seq = ++_companionSeq, uninstall = uninstall ?? new List<string>() };
            foreach (var e in install)
                req.install.Add(new CompanionApp { package = e.app.package, apk_url = e.app.apk_url, sha256 = e.app.sha256, bytes = e.app.bytes, project_id = e.project.id });
            string path = Path.Combine(CompanionDir, "requests.json");
            File.WriteAllText(path + ".tmp", JsonUtility.ToJson(req, true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(path + ".tmp", path);   // the companion never sees a half-written file
        }

        IEnumerator CompanionLoop()
        {
            var wait = new WaitForSeconds(2f);
            long lastSeen = 0;
            while (true)
            {
                yield return wait;
                if (Mode != InstallMode.Companion) continue;
                string path = Path.Combine(CompanionDir, "status.json");
                if (!File.Exists(path)) continue;
                CompanionStatus st;
                try { st = JsonUtility.FromJson<CompanionStatus>(File.ReadAllText(path)); }
                catch { continue; }
                if (st == null || st.updated == lastSeen) continue;
                lastSeen = st.updated;
                if (!string.IsNullOrEmpty(st.message)) Status = "Companion: " + st.message;
                RefreshInstalled();
            }
        }
    }
}
