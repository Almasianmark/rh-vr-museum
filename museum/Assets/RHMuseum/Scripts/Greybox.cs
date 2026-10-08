using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace RHMuseum
{
    /// <summary>Shared materials. Assets live in Resources (created by RH Museum/Setup Project) so the
    /// shaders are included in builds; Shader.Find is only an editor fallback.</summary>
    public static class MuseumMaterials
    {
        static readonly Dictionary<Color, Material> _greybox = new Dictionary<Color, Material>();
        static Shader _greyboxShader, _rippleShader, _fadeShader;

        static Shader Load(string resource, string shaderName)
        {
            var m = Resources.Load<Material>(resource);
            if (m != null) return m.shader;
            var s = Shader.Find(shaderName);
            if (s == null) Debug.LogError($"[RHMuseum] shader {shaderName} missing. Run 'RH Museum/Setup Project'.");
            return s;
        }

        public static Shader RippleShader => _rippleShader ? _rippleShader : (_rippleShader = Load("RHM_Ripple", "RHMuseum/PortalRipple"));
        public static Shader FadeShader => _fadeShader ? _fadeShader : (_fadeShader = Load("RHM_Fade", "RHMuseum/Fade"));

        public static Material Greybox(Color c)
        {
            if (_greybox.TryGetValue(c, out var m) && m != null) return m;
            if (_greyboxShader == null) _greyboxShader = Load("RHM_Greybox", "RHMuseum/Greybox");
            m = new Material(_greyboxShader) { enableInstancing = true, name = $"Greybox {ColorUtility.ToHtmlStringRGB(c)}" };
            m.SetColor("_BaseColor", c);
            _greybox[c] = m;
            return m;
        }
    }

    public static class Palette
    {
        public static readonly Color Floor = new Color(0.42f, 0.43f, 0.46f);
        public static readonly Color Wall = new Color(0.86f, 0.85f, 0.82f);
        public static readonly Color ArchiveWall = new Color(0.70f, 0.72f, 0.78f);
        public static readonly Color Trim = new Color(0.25f, 0.26f, 0.30f);
        public static readonly Color Plaque = new Color(0.14f, 0.15f, 0.18f);
        public static readonly Color Sky = new Color(0.55f, 0.66f, 0.80f);
        public static readonly Color Text = new Color(0.10f, 0.10f, 0.12f);
        public static readonly Color TextOnDark = new Color(0.95f, 0.95f, 0.95f);

        public static Color Fidelity(string f)
        {
            switch (f)
            {
                case "Native": return new Color(0.20f, 0.70f, 0.35f);
                case "Ported": return new Color(0.20f, 0.45f, 0.85f);
                case "Ported-reduced": return new Color(0.15f, 0.65f, 0.70f);
                case "Simulated": return new Color(0.90f, 0.60f, 0.15f);
                case "Browser": return new Color(0.55f, 0.35f, 0.80f);
                default: return new Color(0.50f, 0.50f, 0.52f);  // Watch
            }
        }

        public static string FidelityBlurb(string f)
        {
            switch (f)
            {
                case "Native": return "Runs natively on Quest";
                case "Ported": return "Ported to Quest";
                case "Ported-reduced": return "Ported to Quest with reduced features";
                case "Simulated": return "Custom hardware is simulated on Quest";
                case "Browser": return "Opens in Quest Browser";
                default: return "Watch: demo video only";
            }
        }
    }

    /// <summary>Box/wall/text helpers for procedural greybox geometry.</summary>
    public static class Greybox
    {
        public const float WallThickness = 0.2f;

        public static GameObject Box(Transform parent, string name, Vector3 localCenter, Vector3 size, Color color, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localCenter;
            go.transform.localScale = size;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = MuseumMaterials.Greybox(color);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (!collider) Object.Destroy(go.GetComponent<Collider>());
            go.isStatic = true;
            return go;
        }

        /// <summary>Wall along local X from x0 to x1 at z, optionally with door openings (centers on X).</summary>
        public static void WallX(Transform parent, float x0, float x1, float z, float height, Color color,
                                 IList<float> doorCenters = null, float doorWidth = 2.4f, float doorHeight = 2.8f)
        {
            var cuts = new List<(float a, float b)>();
            if (doorCenters != null)
                foreach (var c in doorCenters) cuts.Add((c - doorWidth / 2, c + doorWidth / 2));
            cuts.Sort((p, q) => p.a.CompareTo(q.a));

            float cursor = x0;
            foreach (var (a, b) in cuts)
            {
                if (a > cursor) Segment(parent, cursor, a, z, 0, height, color);
                Segment(parent, a, b, z, doorHeight, height, color);  // lintel
                cursor = Mathf.Max(cursor, b);
            }
            if (x1 > cursor) Segment(parent, cursor, x1, z, 0, height, color);
        }

        /// <summary>Wall along local Z from z0 to z1 at x (same as WallX, rotated).</summary>
        public static void WallZ(Transform parent, float z0, float z1, float x, float height, Color color,
                                 IList<float> doorCenters = null, float doorWidth = 2.4f, float doorHeight = 2.8f)
        {
            var holder = new GameObject("WallZ").transform;
            holder.SetParent(parent, false);
            holder.localPosition = new Vector3(x, 0, 0);
            holder.localRotation = Quaternion.Euler(0, -90, 0);   // local X of holder = parent +Z
            WallX(holder, z0, z1, 0, height, color, doorCenters, doorWidth, doorHeight);
        }

        static void Segment(Transform parent, float a, float b, float z, float y0, float y1, Color color)
        {
            if (b - a < 0.01f || y1 - y0 < 0.01f) return;
            Box(parent, "Wall", new Vector3((a + b) / 2, (y0 + y1) / 2, z), new Vector3(b - a, y1 - y0, WallThickness), color);
        }

        public static void Floor(Transform parent, Rect xz, Color color)
        {
            Box(parent, "Floor", new Vector3(xz.center.x, -0.05f, xz.center.y), new Vector3(xz.width, 0.1f, xz.height), color);
        }

        public static TextMeshPro Label(Transform parent, string text, Vector3 localPos, Quaternion localRot,
                                        float fontSize, float width, Color color,
                                        TextAlignmentOptions align = TextAlignmentOptions.Center, float height = 1f)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = align;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.rectTransform.sizeDelta = new Vector2(width, height);
            return tmp;
        }
    }
}
