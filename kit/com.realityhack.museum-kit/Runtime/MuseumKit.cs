using System;
using UnityEngine;
using UnityEngine.XR;

namespace RealityHack.MuseumKit
{
    /// <summary>Per-port settings written by the factory to Assets/RHKitGenerated/Resources/rhkit.json.</summary>
    [Serializable]
    public class KitConfig
    {
        public string projectId;
        public string title;
        public string team;
        public int year;
        public string license;
        public string repoUrl;
        public string museumPackage = "world.realityhack.museum";
        public string museumActivity = "com.unity3d.player.UnityPlayerActivity";
        public float splashSeconds = 2.5f;
        public float returnHoldSeconds = 1.5f;
        public bool passthrough;          // AR ports (phone AR, HoloLens, Magic Leap, AR glasses)

        static KitConfig _loaded;
        static bool _tried;

        public static KitConfig Load()
        {
            if (_tried) return _loaded;
            _tried = true;
            var asset = Resources.Load<TextAsset>("rhkit");
            if (asset == null) return null;
            try { _loaded = JsonUtility.FromJson<KitConfig>(asset.text); }
            catch (Exception e) { Debug.LogWarning($"[RHKit] bad rhkit.json: {e.Message}"); }
            return _loaded;
        }
    }

    /// <summary>Spawns the kit in every ported build without touching the project's scenes.</summary>
    public static class KitBootstrap
    {
        public static GameObject Root { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            var cfg = KitConfig.Load();
            if (cfg == null || Root != null) return;
            Root = new GameObject("RealityHack Museum Kit");
            UnityEngine.Object.DontDestroyOnLoad(Root);
            var overlay = Root.AddComponent<KitOverlay>();
            overlay.Init(cfg);
            Root.AddComponent<MuseumReturn>().Init(cfg, overlay);
            if (cfg.passthrough) Root.AddComponent<PassthroughCompat>();
        }
    }

    /// <summary>
    /// Return gesture. CLAUDE.md asks for "both menu buttons", but a Quest has one app menu button
    /// (the right one is the system button), so the gesture is left Menu + right B held together
    /// for returnHoldSeconds. On desktop: hold Escape.
    /// </summary>
    public class MuseumReturn : MonoBehaviour
    {
        KitConfig _cfg;
        KitOverlay _overlay;
        float _held;
        bool _returning;

        public void Init(KitConfig cfg, KitOverlay overlay)
        {
            _cfg = cfg;
            _overlay = overlay;
        }

        void Update()
        {
            if (_returning || _cfg == null) return;
            bool holding = Pressed(XRNode.LeftHand, CommonUsages.menuButton) && Pressed(XRNode.RightHand, CommonUsages.secondaryButton);
#if ENABLE_LEGACY_INPUT_MANAGER
            holding |= Input.GetKey(KeyCode.Escape);
#endif
            _held = holding ? _held + Time.unscaledDeltaTime : 0f;
            _overlay.SetReturnProgress(_held / _cfg.returnHoldSeconds);
            if (_held >= _cfg.returnHoldSeconds)
            {
                _returning = true;
                StartCoroutine(_overlay.FadeThen(() => ReturnToMuseum(_cfg)));
            }
        }

        static bool Pressed(XRNode node, InputFeatureUsage<bool> usage) =>
            InputDevices.GetDeviceAtXRNode(node).TryGetFeatureValue(usage, out bool v) && v;

        /// <summary>Relaunch the museum with returnTo=&lt;projectId&gt; so it spawns at this painting.</summary>
        public static void ReturnToMuseum(KitConfig cfg)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var pm = activity.Call<AndroidJavaObject>("getPackageManager"))
                {
                    // getLaunchIntentForPackage needs package visibility (<queries>) on Android 11+;
                    // the explicit component fallback works without it.
                    AndroidJavaObject intent = pm.Call<AndroidJavaObject>("getLaunchIntentForPackage", cfg.museumPackage);
                    if (intent == null)
                    {
                        intent = new AndroidJavaObject("android.content.Intent", "android.intent.action.MAIN");
                        intent.Call<AndroidJavaObject>("setClassName", cfg.museumPackage, cfg.museumActivity);
                    }
                    using (intent)
                    {
                        intent.Call<AndroidJavaObject>("putExtra", "returnTo", cfg.projectId);
                        intent.Call<AndroidJavaObject>("addFlags", 0x10000000 | 0x04000000);   // NEW_TASK | CLEAR_TOP
                        activity.Call("startActivity", intent);
                    }
                    activity.Call("finish");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[RHKit] could not relaunch {cfg.museumPackage}: {e.Message}");
                return;
            }
#endif
            Debug.Log($"[RHKit] return to museum (returnTo={cfg.projectId})");
            Application.Quit();
        }
    }

    /// <summary>
    /// Branded splash + credits at launch (masks the OS app-switch), return-hold progress bar, fade out.
    /// Uses TextMesh and a built-in font so it works in 2019–6000 projects with or without TextMeshPro.
    /// </summary>
    public class KitOverlay : MonoBehaviour
    {
        Camera _cam;
        Transform _anchor;
        Material _bgMat, _barMat;
        MeshRenderer _bg, _bar;
        TextMesh _text;
        float _alpha = 1f, _progress;
        KitConfig _cfg;

        public void Init(KitConfig cfg)
        {
            _cfg = cfg;
            var shader = Resources.Load<Shader>("RHKitOverlay");
            if (shader == null) shader = Shader.Find("Hidden/RHKit/Overlay");
            _anchor = new GameObject("RHKit Overlay").transform;
            _anchor.SetParent(transform, false);

            _bgMat = new Material(shader);
            _bg = Quad("Backdrop", _bgMat, new Vector3(0, 0, 0.3f), new Vector3(3, 3, 1));
            _barMat = new Material(shader);
            _bar = Quad("Progress", _barMat, new Vector3(0, -0.12f, 0.29f), new Vector3(0.001f, 0.012f, 1));

            var textGo = new GameObject("Credits");
            textGo.transform.SetParent(_anchor, false);
            textGo.transform.localPosition = new Vector3(0, 0.02f, 0.28f);
            _text = textGo.AddComponent<TextMesh>();
            _text.font = BuiltinFont();
            if (_text.font != null)
            {
                var r = textGo.GetComponent<MeshRenderer>();
                r.sharedMaterial = _text.font.material;
                r.sharedMaterial.renderQueue = 5000;
            }
            _text.anchor = TextAnchor.MiddleCenter;
            _text.alignment = TextAlignment.Center;
            _text.characterSize = 0.0045f;
            _text.fontSize = 48;
            _text.text = $"REALITY HACK VR MUSEUM\n\n{cfg.title} ({cfg.year})\n{cfg.team}\n\n{cfg.license} · {cfg.repoUrl}\n\nHold Menu + B to return to the museum";
            StartCoroutine(Splash());
        }

        static Font BuiltinFont()
        {
            foreach (var name in new[] { "LegacyRuntime.ttf", "Arial.ttf" })
            {
                try
                {
                    var f = Resources.GetBuiltinResource<Font>(name);
                    if (f != null) return f;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        MeshRenderer Quad(string name, Material mat, Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_anchor, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            return r;
        }

        System.Collections.IEnumerator Splash()
        {
            _alpha = 1f;
            yield return new WaitForSecondsRealtime(_cfg.splashSeconds);
            for (float t = 0; t < 0.6f; t += Time.unscaledDeltaTime)
            {
                _alpha = 1f - t / 0.6f;
                yield return null;
            }
            _alpha = 0f;
        }

        public System.Collections.IEnumerator FadeThen(Action then)
        {
            _text.text = "Returning to the museum…";
            for (float t = 0; t < 0.5f; t += Time.unscaledDeltaTime)
            {
                _alpha = Mathf.Max(_alpha, t / 0.5f);
                yield return null;
            }
            _alpha = 1f;
            yield return null;
            then();
        }

        public void SetReturnProgress(float p) => _progress = Mathf.Clamp01(p);

        void LateUpdate()
        {
            // Follow whichever camera is current (projects swap cameras between scenes).
            if (_cam == null || !_cam.isActiveAndEnabled) _cam = Camera.main;
            if (_cam != null && _anchor.parent != _cam.transform)
            {
                _anchor.SetParent(_cam.transform, false);
                _anchor.localPosition = Vector3.zero;
                _anchor.localRotation = Quaternion.identity;
            }
            // Layout is designed at ~0.28 m; push it out (uniformly) past cameras with a far near-plane
            // (Unity's default 0.3 m is common in non-XR projects).
            if (_cam != null) _anchor.localScale = Vector3.one * Mathf.Max(1f, (_cam.nearClipPlane + 0.03f) / 0.28f);

            bool showText = _alpha > 0.5f;
            _bgMat.color = new Color(0.05f, 0.05f, 0.08f, _alpha);
            _bg.enabled = _alpha > 0.001f;
            _text.gameObject.SetActive(showText);

            _bar.enabled = _progress > 0.01f;
            _barMat.color = new Color(0.95f, 0.76f, 0.3f, 1f);
            _bar.transform.localScale = new Vector3(0.24f * _progress, 0.012f, 1);
        }
    }

    /// <summary>
    /// AR ports (phone AR / HoloLens / Magic Leap / AR glasses): show the room behind the content.
    /// Uses AR Foundation + Meta OpenXR (added to the manifest by the factory) via reflection, so the kit
    /// compiles in projects without them. Camera clears to transparent black so passthrough shows through;
    /// on additive-display ports, black already meant "see-through".
    /// </summary>
    public class PassthroughCompat : MonoBehaviour
    {
        void Start()
        {
            var session = FindType("UnityEngine.XR.ARFoundation.ARSession, Unity.XR.ARFoundation");
            var camMgr = FindType("UnityEngine.XR.ARFoundation.ARCameraManager, Unity.XR.ARFoundation");
            if (session == null || camMgr == null)
            {
                Debug.LogWarning("[RHKit] passthrough requested but AR Foundation is not in this build");
                return;
            }
            if (FindObjectOfType(session) == null) new GameObject("AR Session (RHKit)").AddComponent(session);
            var cam = Camera.main;
            if (cam == null) return;
            if (cam.GetComponent(camMgr) == null) cam.gameObject.AddComponent(camMgr);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
        }

        static Type FindType(string name) => Type.GetType(name, false);
    }
}
