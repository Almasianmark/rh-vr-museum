using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR.Features.Extensions.PerformanceSettings;

namespace RHMuseum
{
    /// <summary>
    /// Headset-side render settings. Foveation needs the OpenXR "Foveated Rendering" feature enabled for Android
    /// (Foveated Rendering API = SRP Foveation); the GPU hint needs the "XR Performance Settings" feature.
    /// Both features are on in Assets/XR/Settings/OpenXR Package Settings.asset.
    /// </summary>
    public static class XRPlatform
    {
        static readonly List<XRDisplaySubsystem> _displays = new List<XRDisplaySubsystem>();

        /// <summary>0 = off, 1 = full strength (Quest maps it to its discrete off/low/medium/high levels).
        /// Returns the level the display reports back, or -1 with no XR display.</summary>
        public static float SetFoveation(float level)
        {
            SubsystemManager.GetSubsystems(_displays);
            if (_displays.Count == 0) return -1;
            foreach (var d in _displays)
            {
                d.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.None;   // Quest 2 has no eye tracking
                d.foveatedRenderingLevel = level;
            }
            return _displays[0].foveatedRenderingLevel;
        }

        /// <summary>Boost asks the runtime for its highest GPU clocks (may throttle when hot); otherwise sustained-high.</summary>
        public static bool SetGpuBoost(bool boost) =>
            XrPerformanceSettingsFeature.SetPerformanceLevelHint(PerformanceDomain.Gpu,
                boost ? PerformanceLevelHint.Boost : PerformanceLevelHint.SustainedHigh);

        /// <summary>GPU time of the last frame in ms, if the XR provider reports it.</summary>
        public static bool TryGetGpuMs(out float ms)
        {
            ms = 0;
            SubsystemManager.GetSubsystems(_displays);
            if (_displays.Count == 0) return false;
            // Names from the OpenXR plugin's Meta performance-metrics tests: the first is seconds, the raw one ms.
            if (UnityEngine.XR.Provider.XRStats.TryGetStat(_displays[0], "GPUAppLastFrameTime", out float seconds) && seconds > 0)
                ms = seconds * 1000f;
            else if (!UnityEngine.XR.Provider.XRStats.TryGetStat(_displays[0], "perfmetrics.appgputime", out ms))
                return false;
            return ms > 0;
        }
    }
}
