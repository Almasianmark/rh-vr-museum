using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace RHMuseum.Apps
{
    /// <summary>
    /// In-world UI for the install system: per-painting state badges, a status sign at every wing
    /// entrance, a lobby kiosk (install mode, storage budget, preload) and an Install button in the theater.
    /// </summary>
    public class AppUi : MonoBehaviour
    {
        MuseumBuilder _builder;
        VideoTheater _theater;
        readonly Dictionary<MuseumBuilder.WingInfo, TextMeshPro> _wingSigns = new Dictionary<MuseumBuilder.WingInfo, TextMeshPro>();
        TextMeshPro _kioskText, _theaterText;
        TouchButton _theaterInstall;

        public static AppUi Create(MuseumBuilder builder, VideoTheater theater, Transform lobbyParent)
        {
            var ui = new GameObject("App UI").AddComponent<AppUi>();
            ui._builder = builder;
            ui._theater = theater;
            ui.BuildWingSigns();
            ui.BuildKiosk(lobbyParent);
            ui.BuildTheaterPanel();
            return ui;
        }

        void OnEnable() => AppManager.Changed += OnChanged;
        void OnDisable() => AppManager.Changed -= OnChanged;

        void Start()
        {
            OnChanged(null);
            StartCoroutine(Tick());
        }

        IEnumerator Tick()
        {
            var wait = new WaitForSeconds(1f);
            while (true)
            {
                RefreshSigns();
                yield return wait;
            }
        }

        // ------------------------------------------------------------------ painting badges

        void OnChanged(string package)
        {
            var mgr = AppManager.Instance;
            if (mgr == null) return;
            foreach (var e in mgr.Apps.Values)
            {
                if (package != null && e.app.package != package) continue;
                if (_builder.Paintings.TryGetValue(e.project.id, out var view)) view.SetAppBadge(Badge(e), BadgeColor(e.state));
                if (_theater != null && _theater.Screen != null && _theater.Screen.Info == e.project)
                    _theater.Screen.SetAppBadge(Badge(e), BadgeColor(e.state));
            }
            RefreshTheater();
            RefreshSigns();
        }

        static string Badge(AppManager.Entry e)
        {
            switch (e.state)
            {
                case AppState.Installed: return "INSTALLED · push through to play";
                case AppState.UpdateAvailable: return "UPDATE AVAILABLE · playable";
                case AppState.Downloading: return $"DOWNLOADING {e.progress:P0}";
                case AppState.Verifying: return "VERIFYING SHA-256";
                case AppState.Downloaded: return "READY · installs at the wing entrance";
                case AppState.Installing: return "INSTALLING…";
                case AppState.Queued: return "QUEUED FOR DOWNLOAD";
                case AppState.Failed: return "ERROR · " + e.error;
                default: return "PLAYABLE PORT · approach the wing to download";
            }
        }

        static Color BadgeColor(AppState s)
        {
            switch (s)
            {
                case AppState.Installed: return new Color(0.25f, 0.8f, 0.4f);
                case AppState.Failed: return new Color(0.9f, 0.3f, 0.3f);
                case AppState.Downloaded:
                case AppState.UpdateAvailable: return new Color(0.95f, 0.76f, 0.3f);
                default: return new Color(0.55f, 0.75f, 0.95f);
            }
        }

        // ------------------------------------------------------------------ wing entrance signs

        void BuildWingSigns()
        {
            foreach (var wing in _builder.Wings)
            {
                // On the corridor's north wall, just inside the entrance (wing space x≈2), facing into the corridor.
                var sign = Greybox.Label(wing.root.transform, "", new Vector3(1.6f, 1.7f, MuseumBuilder.CorridorW / 2 - 0.12f),
                    Quaternion.identity, 0.9f, 2.6f, Palette.Text, TextAlignmentOptions.Center, 1.0f);
                _wingSigns[wing] = sign;
            }
        }

        void RefreshSigns()
        {
            var mgr = AppManager.Instance;
            if (mgr == null) return;
            foreach (var kv in _wingSigns)
                if (kv.Key.root.activeInHierarchy) kv.Value.text = mgr.WingSummary(kv.Key);
            if (_kioskText != null) _kioskText.text = KioskText(mgr);
        }

        // ------------------------------------------------------------------ lobby kiosk

        void BuildKiosk(Transform lobby)
        {
            var k = new GameObject("Apps Kiosk").transform;
            k.SetParent(lobby, false);
            k.localPosition = new Vector3(-5.2f, 0, -2.5f);
            k.localRotation = Quaternion.Euler(0, -90, 0);   // faces visitors standing east of it
            Greybox.Box(k, "Stand", new Vector3(0, 0.55f, 0.15f), new Vector3(1.6f, 1.1f, 0.3f), new Color(0.3f, 0.32f, 0.36f));
            _kioskText = Greybox.Label(k, "", new Vector3(0, 2.0f, 0.1f), Quaternion.identity, 0.55f, 1.6f,
                Palette.Text, TextAlignmentOptions.TopLeft, 0.7f);

            Color blue = new Color(0.25f, 0.45f, 0.75f), grey = new Color(0.35f, 0.36f, 0.4f), green = new Color(0.2f, 0.55f, 0.3f);
            Quaternion tilt = Quaternion.Euler(35, 0, 0);
            var mgr = AppManager.Instance;
            TouchButton.Create(k, "Standalone", new Vector3(-0.42f, 1.45f, 0.05f), tilt, new Vector2(0.38f, 0.14f), blue, () => mgr.SetMode(InstallMode.Standalone));
            TouchButton.Create(k, "Companion", new Vector3(0.0f, 1.45f, 0.05f), tilt, new Vector2(0.38f, 0.14f), blue, () => mgr.SetMode(InstallMode.Companion));
            TouchButton.Create(k, "Preload", new Vector3(0.42f, 1.45f, 0.05f), tilt, new Vector2(0.38f, 0.14f), green, () => mgr.Preload());
            TouchButton.Create(k, "- 2 GB", new Vector3(-0.21f, 1.25f, 0.12f), tilt, new Vector2(0.38f, 0.14f), grey, () => mgr.SetBudgetGb(mgr.BudgetBytes / (float)(1L << 30) - 2));
            TouchButton.Create(k, "+ 2 GB", new Vector3(0.21f, 1.25f, 0.12f), tilt, new Vector2(0.38f, 0.14f), grey, () => mgr.SetBudgetGb(mgr.BudgetBytes / (float)(1L << 30) + 2));
        }

        static string KioskText(AppManager mgr)
        {
            const float GB = 1L << 30;
            int installed = mgr.Apps.Values.Count(e => e.state == AppState.Installed || e.state == AppState.UpdateAvailable);
            return $"<b>Apps & storage</b>\nMode: <b>{mgr.Mode}</b> · {mgr.Apps.Count} playable ports · {installed} installed\n" +
                   $"Storage: {mgr.UsedBytes / GB:0.0} of {mgr.BudgetBytes / GB:0} GB budget" +
                   (string.IsNullOrEmpty(mgr.Status) ? "" : $"\n<color=#B8860B>{mgr.Status}</color>");
        }

        // ------------------------------------------------------------------ theater

        void BuildTheaterPanel()
        {
            if (_theater == null) return;
            var t = _theater.transform;
            _theaterText = Greybox.Label(t, "", new Vector3(0, 1.74f, 0.48f), Quaternion.Euler(35, 0, 0), 0.5f, 1.6f,
                Palette.TextOnDark, TextAlignmentOptions.Center, 0.18f);
            _theaterInstall = TouchButton.Create(t, "Install & play", new Vector3(0, 1.56f, 0.40f), Quaternion.Euler(35, 0, 0),
                new Vector2(0.6f, 0.16f), new Color(0.2f, 0.45f, 0.75f), () =>
                {
                    var mgr = AppManager.Instance;
                    var p = _theater.Current;
                    if (mgr == null || p == null) return;
                    if (!mgr.Launch(p)) mgr.InstallNow(p);
                });
        }

        void RefreshTheater()
        {
            if (_theaterInstall == null) return;
            var mgr = AppManager.Instance;
            var e = mgr != null && _theater.Current != null ? mgr.For(_theater.Current) : null;
            _theaterInstall.gameObject.SetActive(e != null);
            _theaterText.text = e == null ? "" : Badge(e);
        }

        public void OnTheaterShown() => RefreshTheater();
    }
}
