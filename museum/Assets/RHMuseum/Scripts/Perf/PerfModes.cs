using System;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace RHMuseum
{
    /// <summary>The individual Quest 2 frame-rate fixes, so each one can be measured on its own.</summary>
    [Flags]
    public enum PerfFix
    {
        None = 0,
        Foveation = 1 << 0,    // fixed foveated rendering, high
        Msaa2x = 1 << 1,       // 2x MSAA instead of the URP asset's 4x
        RippleIdle = 1 << 2,   // untouched paintings: 4-vertex quad + 1-sample shader variant
        TextCull = 1 << 3,     // painting text hidden beyond reading distance; TMP mobile SDF shader
        RoomCull = 1 << 4,     // paintings hidden unless visible through their room's doorway
        GpuBoost = 1 << 5,     // OpenXR performance hint: GPU Boost instead of Sustained High
        Shipping = Foveation | Msaa2x | RippleIdle | TextCull | RoomCull,
    }

    /// <summary>
    /// Frame-rate A/B switch for headset testing. Click the left thumbstick (desktop: P) to cycle modes.
    /// Every switch logs "[RHPerf] mode=..." and every 2 s "[RHPerf] stats ..." to logcat (tag Unity), so one
    /// 60 s capture attributes the gain of each fix. A head-locked label shows the mode for 3 s after a switch.
    /// Starts in "shipping" (every fix except GPU boost). Remove this switch once the numbers are in.
    /// </summary>
    public class PerfModes : MonoBehaviour
    {
        static readonly (string name, PerfFix fixes)[] Modes =
        {
            ("shipping", PerfFix.Shipping),
            ("legacy", PerfFix.None),
            ("foveation", PerfFix.Foveation),
            ("msaa2x", PerfFix.Msaa2x),
            ("ripple-idle", PerfFix.RippleIdle),
            ("text-cull", PerfFix.TextCull),
            ("room-cull", PerfFix.RoomCull),
            ("shipping+gpu-boost", PerfFix.Shipping | PerfFix.GpuBoost),
        };

        public static PerfFix Active { get; private set; } = PerfFix.Shipping;
        public static bool Has(PerfFix fix) => (Active & fix) != 0;
        public static event Action Changed;

        const float StatsInterval = 2f;

        int _mode;
        Camera _head;
        TextMeshPro _hud;
        float _hudUntil;
        bool _stickHeld;
        int _originalMsaa = -1;

        // Stats window
        float _windowStart, _worstDt, _gpuSum;
        int _frames, _gpuSamples, _slowFrames;

        public static PerfModes Create(Camera head)
        {
            var go = new GameObject("PerfModes");
            var pm = go.AddComponent<PerfModes>();
            pm._head = head;
            return pm;
        }

        void Start()
        {
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp) _originalMsaa = urp.msaaSampleCount;
            _hud = Greybox.Label(_head.transform, "", new Vector3(0, -0.22f, 0.9f), Quaternion.identity, 0.35f, 0.9f,
                new Color(1f, 0.9f, 0.3f), TextAlignmentOptions.Center, 0.12f);
            _hud.gameObject.SetActive(false);
            Debug.Log($"[RHPerf] start device={SystemInfo.deviceModel} gpu={SystemInfo.graphicsDeviceName} " +
                      $"api={SystemInfo.graphicsDeviceType} xr={XRSettings.isDeviceActive} refresh={RefreshRate()}Hz " +
                      $"eye={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} msaa={_originalMsaa}");
            Apply(0);
            ResetWindow();
        }

        void Update()
        {
            if (CyclePressed()) Apply((_mode + 1) % Modes.Length);

            // Stats
            float dt = Time.unscaledDeltaTime;
            _frames++;
            _worstDt = Mathf.Max(_worstDt, dt);
            if (dt > 1.5f / 72f) _slowFrames++;
            if (XRPlatform.TryGetGpuMs(out float gpu)) { _gpuSum += gpu; _gpuSamples++; }
            float elapsed = Time.unscaledTime - _windowStart;
            if (elapsed >= StatsInterval)
            {
                string gpuText = _gpuSamples > 0 ? $"{_gpuSum / _gpuSamples:F1}ms" : "n/a";
                Debug.Log($"[RHPerf] stats mode={Modes[_mode].name} fps={_frames / elapsed:F1} min={1f / Mathf.Max(_worstDt, 1e-4f):F0} " +
                          $"slow={_slowFrames}/{_frames} gpu={gpuText} pos={Pos()}");
                ResetWindow();
            }

            if (_hud.gameObject.activeSelf && Time.unscaledTime > _hudUntil) _hud.gameObject.SetActive(false);
        }

        void ResetWindow()
        {
            _windowStart = Time.unscaledTime;
            _frames = _gpuSamples = _slowFrames = 0;
            _worstDt = _gpuSum = 0;
        }

        void Apply(int mode)
        {
            _mode = mode;
            Active = Modes[mode].fixes;

            float fov = XRPlatform.SetFoveation(Has(PerfFix.Foveation) ? 1f : 0f);
            bool boost = XRPlatform.SetGpuBoost(Has(PerfFix.GpuBoost));
            int msaa = _originalMsaa;
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp && _originalMsaa > 0)
            {
                msaa = Has(PerfFix.Msaa2x) ? Mathf.Min(2, _originalMsaa) : _originalMsaa;
                urp.msaaSampleCount = msaa;   // URP pushes this to the XR display every frame
            }
            Greybox.SetLiteText(Has(PerfFix.TextCull));
            PaintingView.RefreshAllSurfaces();
            Changed?.Invoke();

            Debug.Log($"[RHPerf] mode={Modes[mode].name} ({mode + 1}/{Modes.Length}) fixes={Active} " +
                      $"foveation={(fov < 0 ? "no-xr" : fov.ToString("F2"))} msaa={msaa} gpuHint={(boost ? (Has(PerfFix.GpuBoost) ? "boost" : "sustained-high") : "unavailable")} pos={Pos()}");
            _hud.text = $"PERF {mode + 1}/{Modes.Length}: {Modes[mode].name}";
            _hud.gameObject.SetActive(true);
            _hudUntil = Time.unscaledTime + 3f;
            ResetWindow();
        }

        bool CyclePressed()
        {
            bool pressed = DesktopInput.PerfPressed();
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            bool held = left.isValid && left.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool click) && click;
            if (held && !_stickHeld) pressed = true;
            _stickHeld = held;
            return pressed;
        }

        string Pos()
        {
            Vector3 p = _head.transform.position;
            return $"{p.x:F0},{p.z:F0}";
        }

        static string RefreshRate()
        {
            var displays = new System.Collections.Generic.List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            return displays.Count > 0 && displays[0].TryGetDisplayRefreshRate(out float hz) ? hz.ToString("F0") : "?";
        }

        void OnDestroy()
        {
            // In the editor, msaaSampleCount writes through to the asset on disk: put it back.
            if (_originalMsaa > 0 && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
                urp.msaaSampleCount = _originalMsaa;
        }
    }
}
