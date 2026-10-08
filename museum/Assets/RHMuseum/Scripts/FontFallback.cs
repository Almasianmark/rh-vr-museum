using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace RHMuseum
{
    /// <summary>
    /// LiberationSans has no CJK glyphs, so those titles rendered as boxes. On the headset, add the system's CJK
    /// font (Quest OS is Android, which ships Noto CJK) as a dynamic TMP fallback; glyphs are rasterized on first
    /// use only. Android only: in the editor this would write a runtime object into the font asset on disk.
    /// Emoji are stripped from museum.json by the pipeline (TMP SDF can't draw color emoji).
    /// </summary>
    public static class FontFallback
    {
        static readonly string[] Candidates =
        {
            "/system/fonts/NotoSansCJK-Regular.ttc",
            "/system/fonts/NotoSansSC-Regular.otf",
            "/system/fonts/NotoSansCJKsc-Regular.otf",
            "/system/fonts/DroidSansFallback.ttf",
            "/system/fonts/DroidSansFallbackFull.ttf",
        };

        static bool _installed;

        public static void Install()
        {
            if (_installed || Application.platform != RuntimePlatform.Android) return;
            _installed = true;
            var main = TMP_Settings.defaultFontAsset;
            if (main == null) return;
            foreach (var path in Candidates)
            {
                if (!File.Exists(path)) continue;
                var fallback = TMP_FontAsset.CreateFontAsset(path, 0, 64, 6, GlyphRenderMode.SDFAA, 1024, 1024);
                if (fallback == null) continue;
                fallback.name = "System CJK fallback";
                if (main.fallbackFontAssetTable == null) main.fallbackFontAssetTable = new List<TMP_FontAsset>();
                main.fallbackFontAssetTable.Add(fallback);
                Debug.Log($"[RHMuseum] CJK fallback font: {path}");
                return;
            }
            Debug.Log("[RHMuseum] no system CJK font found; CJK text will show as boxes");
        }
    }
}
