using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Video;

namespace RHMuseum
{
    /// <summary>Camera-attached quad that fades the view (jump-in / return transitions).</summary>
    public class ScreenFader : MonoBehaviour
    {
        Material _mat;
        Color _color = new Color(1, 1, 1, 0);
        static readonly int ColorId = Shader.PropertyToID("_Color");

        public static ScreenFader Attach(Camera cam)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "Fader";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(cam.transform, false);
            go.transform.localPosition = new Vector3(0, 0, cam.nearClipPlane + 0.02f);
            go.transform.localScale = new Vector3(2, 2, 1);
            var f = go.AddComponent<ScreenFader>();
            f._mat = new Material(MuseumMaterials.FadeShader);
            go.GetComponent<MeshRenderer>().sharedMaterial = f._mat;
            f.Apply();
            return f;
        }

        void Apply()
        {
            _mat.SetColor(ColorId, _color);
            GetComponent<MeshRenderer>().enabled = _color.a > 0.001f;
        }

        public IEnumerator Fade(float targetAlpha, float duration, Color? color = null)
        {
            if (color.HasValue) _color = new Color(color.Value.r, color.Value.g, color.Value.b, _color.a);
            float start = _color.a;
            for (float t = 0; t < duration; t += Time.deltaTime)
            {
                _color.a = Mathf.Lerp(start, targetAlpha, t / duration);
                Apply();
                yield return null;
            }
            _color.a = targetAlpha;
            Apply();
        }
    }

    /// <summary>A pressable panel (touch with a fingertip or click).</summary>
    public class TouchButton : TouchTarget
    {
        public Action Pressed;
        GameObject _face;
        Color _color;

        public static TouchButton Create(Transform parent, string label, Vector3 localPos, Quaternion rot, Vector2 size, Color color, Action pressed)
        {
            var go = new GameObject($"Button {label}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = rot;
            var b = go.AddComponent<TouchButton>();
            b.size = size;
            b.pushDepth = 0.025f;   // buttons fire on a light press
            b.Pressed = pressed;
            b._color = color;
            b._face = Greybox.Box(go.transform, "Face", new Vector3(0, 0, 0.02f), new Vector3(size.x, size.y, 0.04f), color, false);
            b._face.isStatic = false;
            Greybox.Label(go.transform, $"<b>{label}</b>", new Vector3(0, 0, -0.005f), Quaternion.identity, 0.9f, size.x - 0.04f,
                Palette.TextOnDark, TextAlignmentOptions.Center, size.y);
            b.EnsureCollider();
            return b;
        }

        protected override void OnTouchStart(Vector2 uv) =>
            _face.GetComponent<MeshRenderer>().sharedMaterial = MuseumMaterials.Greybox(Color.Lerp(_color, Color.white, 0.35f));

        protected override void OnPush(Vector2 uv)
        {
            _face.GetComponent<MeshRenderer>().sharedMaterial = MuseumMaterials.Greybox(_color);
            Pressed?.Invoke();
        }
    }

    /// <summary>
    /// Video theater: where Watch projects (and anything not launchable yet) land.
    /// Direct video files play on the screen. YouTube/Vimeo links can't be streamed by VideoPlayer,
    /// so "Play demo" opens them in Quest Browser.
    /// </summary>
    public class VideoTheater : MonoBehaviour
    {
        public static readonly Vector3 Origin = new Vector3(0, 0, -400);
        public PaintingView Screen { get; private set; }
        public Vector3 ViewPoint => Origin + new Vector3(0, 0, -1.5f);
        public event Action ExitRequested;

        TextMeshPro _status;
        VideoPlayer _player;
        RenderTexture _rt;
        ProjectInfo _current;

        public static VideoTheater Create(Transform parent)
        {
            var go = new GameObject("Video Theater");
            go.transform.SetParent(parent, false);
            go.transform.position = Origin;
            var th = go.AddComponent<VideoTheater>();
            th.BuildRoom();
            return th;
        }

        void BuildRoom()
        {
            var t = transform;
            Color wall = new Color(0.18f, 0.19f, 0.23f);
            Greybox.Floor(t, new Rect(-7, -6, 14, 12), new Color(0.12f, 0.12f, 0.15f));
            Greybox.WallX(t, -7, 7, 6, 5, wall);
            Greybox.WallX(t, -7, 7, -6, 5, wall);
            Greybox.WallZ(t, -6, 6, -7, 5, wall);
            Greybox.WallZ(t, -6, 6, 7, 5, wall);

            _status = Greybox.Label(t, "", new Vector3(0, 1.38f, 0.32f), Quaternion.Euler(35, 0, 0), 0.6f, 1.5f, Palette.TextOnDark,
                TextAlignmentOptions.Center, 0.2f);
            TouchButton.Create(t, "Play demo", new Vector3(-0.45f, 1.1f, 0.2f), Quaternion.Euler(35, 0, 0), new Vector2(0.55f, 0.22f),
                new Color(0.2f, 0.55f, 0.3f), Play);
            TouchButton.Create(t, "Back", new Vector3(0.45f, 1.1f, 0.2f), Quaternion.Euler(35, 0, 0), new Vector2(0.55f, 0.22f),
                new Color(0.55f, 0.25f, 0.25f), () => ExitRequested?.Invoke());
            Greybox.Box(t, "Podium", new Vector3(0, 0.5f, 0.3f), new Vector3(1.5f, 1.0f, 0.4f), new Color(0.3f, 0.3f, 0.35f));
        }

        public void Show(ProjectInfo info)
        {
            _current = info;
            if (Screen != null) Destroy(Screen.gameObject);
            // The screen is just a big painting: same ripple surface, full plaque underneath.
            Screen = PaintingView.Create(transform, info, new Vector3(0, 2.75f, 5.8f), Quaternion.identity, 4.8f, false);
            _status.text = string.IsNullOrEmpty(info.video_url)
                ? "No demo video for this project."
                : IsDirectVideo(info.video_url) ? "Touch Play to start the video." : "Play opens the demo video in Quest Browser.";
            StopVideo();
        }

        public void Hide()
        {
            StopVideo();
            if (Screen != null) Destroy(Screen.gameObject);
            Screen = null;
            _current = null;
        }

        static bool IsDirectVideo(string url)
        {
            string u = url.Split('?')[0].ToLowerInvariant();
            return u.EndsWith(".mp4") || u.EndsWith(".webm") || u.EndsWith(".m3u8") || u.EndsWith(".mov");
        }

        public void Play()
        {
            if (_current == null || string.IsNullOrEmpty(_current.video_url)) return;
            if (!IsDirectVideo(_current.video_url))
            {
                Application.OpenURL(_current.video_url);
                return;
            }
            if (_player == null)
            {
                _player = gameObject.AddComponent<VideoPlayer>();
                _player.playOnAwake = false;
                _player.renderMode = VideoRenderMode.RenderTexture;
                _player.audioOutputMode = VideoAudioOutputMode.Direct;
            }
            if (_rt == null) _rt = new RenderTexture(1280, 720, 0);
            _player.targetTexture = _rt;
            _player.url = _current.video_url;
            _player.prepareCompleted += OnPrepared;
            _player.Prepare();
            _status.text = "Loading video…";
        }

        void OnPrepared(VideoPlayer vp)
        {
            vp.prepareCompleted -= OnPrepared;
            vp.Play();
            _status.text = "";
            if (Screen != null) Screen.ShowExternalTexture(_rt);
        }

        void StopVideo()
        {
            if (_player != null && _player.isPlaying) _player.Stop();
        }

        void OnDestroy()
        {
            if (_rt != null) _rt.Release();
        }
    }
}
