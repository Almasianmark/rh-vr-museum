using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace RHMuseum
{
    /// <summary>
    /// Scene entry point: load museum.json, build the greybox museum, spawn the player (at the painting
    /// named by a returnTo intent extra, if any), stream thumbnails, and run jump-in / back transitions.
    /// </summary>
    public class MuseumBootstrap : MonoBehaviour
    {
        [Tooltip("Remote museum.json so a new year needs no rebuild. Empty = cache/StreamingAssets only.")]
        public string remoteMuseumUrl = "https://raw.githubusercontent.com/Almasianmark/rh-vr-museum/main/data/museum.json";
        public float thumbnailLoadRadius = 14f;
        public float thumbnailUnloadRadius = 30f;
        public float wingActivationMargin = 20f;
        public int maxConcurrentDownloads = 2;

        MuseumDoc _doc;
        MuseumBuilder _builder;
        PlayerRig _rig;
        ScreenFader _fader;
        VideoTheater _theater;
        bool _busy, _inTheater;
        (Vector3 pos, float yaw) _returnPose;
        int _downloads;

        IEnumerator Start()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 72;

            _rig = PlayerRig.Create(Vector3.zero, 0);
            _fader = ScreenFader.Attach(_rig.Head);
            StartCoroutine(_fader.Fade(1, 0.01f, Color.black));

            string source = null;
            yield return MuseumDataLoader.Load(remoteMuseumUrl, (doc, src) => { _doc = doc; source = src; });
            if (_doc == null) yield break;
            Debug.Log($"[RHMuseum] museum.json from {source}: {_doc.projects.Count} projects, generated {_doc.generated_at}");

            var root = new GameObject("Museum").transform;
            _builder = new MuseumBuilder(_doc, root);
            _builder.Build();
            _theater = VideoTheater.Create(root);
            _theater.ExitRequested += Back;
            _theater.gameObject.SetActive(false);

            PaintingView.PushedThrough += OnPushedThrough;
            _rig.BackPressed += Back;

            string returnTo = LaunchArgs.ReturnTo();
            if (!string.IsNullOrEmpty(returnTo) && _builder.Paintings.TryGetValue(returnTo, out var painting))
            {
                var (pos, yaw) = MuseumBuilder.ViewpointFor(painting);
                _rig.TeleportTo(pos, yaw);
            }
            else _rig.TeleportTo(_builder.SpawnPoint, _builder.SpawnYaw);

            StartCoroutine(StreamLoop());
            yield return _fader.Fade(0, 0.6f);
        }

        void OnDestroy()
        {
            PaintingView.PushedThrough -= OnPushedThrough;
        }

        // ------------------------------------------------------------------ transitions

        void OnPushedThrough(PaintingView view, Vector2 uv)
        {
            if (_busy) return;
            if (_inTheater && view == _theater.Screen)
            {
                _theater.Play();
                return;
            }
            StartCoroutine(JumpIn(view, uv));
        }

        IEnumerator JumpIn(PaintingView view, Vector2 uv)
        {
            _busy = true;
            var info = view.Info;
            StartCoroutine(view.AnimateEnter(uv, 0.9f));
            yield return _fader.Fade(1, 0.9f, Color.white);

            string kind = info.launch != null ? info.launch.kind : "theater";
            string url = info.launch != null ? info.launch.url : "";
            if ((kind == "browser" || kind == "horizon") && !string.IsNullOrEmpty(url))
            {
                // Quest switches to Browser / Horizon; when the user comes back they're still at this painting.
                Application.OpenURL(url);
                view.SetEnter(0, uv);
                yield return _fader.Fade(0, 0.5f);
            }
            else
            {
                // Native / ported builds aren't installed yet (build-order step 5), so everything else
                // goes to the theater for now.
                _returnPose = MuseumBuilder.ViewpointFor(view);
                _theater.gameObject.SetActive(true);
                _theater.Show(info);
                _rig.TeleportTo(_theater.ViewPoint, 0);
                _inTheater = true;
                view.SetEnter(0, uv);
                yield return _fader.Fade(0, 0.6f);
            }
            _busy = false;
        }

        void Back()
        {
            if (_busy) return;
            StartCoroutine(BackRoutine());
        }

        IEnumerator BackRoutine()
        {
            _busy = true;
            yield return _fader.Fade(1, 0.4f, Color.black);
            if (_inTheater)
            {
                _theater.Hide();
                _theater.gameObject.SetActive(false);
                _inTheater = false;
                _rig.TeleportTo(_returnPose.pos, _returnPose.yaw);
            }
            else _rig.TeleportTo(_builder.SpawnPoint, _builder.SpawnYaw);   // back in the museum = go to lobby
            yield return _fader.Fade(0, 0.4f);
            _busy = false;
        }

        // ------------------------------------------------------------------ streaming

        IEnumerator StreamLoop()
        {
            var wait = new WaitForSeconds(0.25f);
            while (true)
            {
                Vector3 p = _rig.Head.transform.position;
                foreach (var wing in _builder.Wings)
                {
                    bool near = !_inTheater && wing.bounds.SqrDistance(p) < wingActivationMargin * wingActivationMargin;
                    if (wing.root.activeSelf != near)
                    {
                        wing.root.SetActive(near);
                        if (!near) foreach (var v in wing.paintings) v.ReleaseThumbnail();
                    }
                    if (!near) continue;

                    foreach (var v in wing.paintings.OrderBy(v => (v.transform.position - p).sqrMagnitude))
                    {
                        float d = Vector3.Distance(v.transform.position, p);
                        if (d > thumbnailUnloadRadius && v.ThumbnailLoaded) v.ReleaseThumbnail();
                        else if (d < thumbnailLoadRadius && v.WantsThumbnail && !v.ThumbnailRequested && _downloads < maxConcurrentDownloads)
                            StartCoroutine(LoadThumbnail(v));
                    }
                }
                if (_inTheater && _theater.Screen != null && !_theater.Screen.ThumbnailRequested && _theater.Screen.WantsThumbnail)
                    StartCoroutine(LoadThumbnail(_theater.Screen));
                yield return wait;
            }
        }

        static readonly Dictionary<string, byte[]> _bytesCache = new Dictionary<string, byte[]>();

        IEnumerator LoadThumbnail(PaintingView v)
        {
            v.ThumbnailRequested = true;
            string url = v.Info.thumbnail;
            if (!_bytesCache.TryGetValue(url, out var bytes))
            {
                _downloads++;
                using (var req = UnityWebRequest.Get(url))
                {
                    req.timeout = 15;
                    yield return req.SendWebRequest();
                    _downloads--;
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning($"[RHMuseum] thumbnail failed for {v.Info.id}: {req.error}");
                        yield break;   // stays requested: no retry storm
                    }
                    bytes = req.downloadHandler.data;
                }
                if (_bytesCache.Count > 200) _bytesCache.Clear();
                _bytesCache[url] = bytes;
            }
            if (v == null || !v.ThumbnailRequested) yield break;   // released while downloading

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!tex.LoadImage(bytes, false))
            {
                Destroy(tex);
                yield break;
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 4;
            tex.Apply(true, true);   // build mips (no shimmer in the headset), drop the CPU copy
            v.SetThumbnail(tex);
        }
    }

    /// <summary>Reads the returnTo project id: Android intent extra (MuseumReturn) or -returnTo=id on desktop.</summary>
    public static class LaunchArgs
    {
        public static string ReturnTo()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
                {
                    return intent.Call<string>("getStringExtra", "returnTo");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[RHMuseum] intent read failed: {e.Message}");
                return null;
            }
#else
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (arg.StartsWith("-returnTo=")) return arg.Substring("-returnTo=".Length);
            return null;
#endif
        }
    }
}
