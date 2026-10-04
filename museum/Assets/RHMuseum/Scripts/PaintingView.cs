using System;
using System.Collections;
using TMPro;
using UnityEngine;

namespace RHMuseum
{
    /// <summary>
    /// One painting: rippling canvas (thumbnail), frame colored by fidelity, plaque with synopsis.
    /// Touch = ripple, push through = jump into the project.
    /// Root transform: +Z points into the wall; the canvas faces -Z toward the visitor.
    /// </summary>
    public class PaintingView : TouchTarget
    {
        public ProjectInfo Info { get; private set; }
        public Texture2D Thumbnail { get; private set; }
        public bool WantsThumbnail => !string.IsNullOrEmpty(Info?.thumbnail);
        public bool ThumbnailLoaded => Thumbnail != null;
        public bool ThumbnailRequested { get; set; }

        public static event Action<PaintingView, Vector2> PushedThrough;

        const int MaxRipples = 4;
        static Mesh _sharedGrid;
        static Texture2D _placeholder;
        static readonly int RipplesId = Shader.PropertyToID("_Ripples");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int TintId = Shader.PropertyToID("_Tint");
        static readonly int AspectId = Shader.PropertyToID("_Aspect");
        static readonly int EnterId = Shader.PropertyToID("_Enter");
        static readonly int EnterOriginId = Shader.PropertyToID("_EnterOrigin");

        readonly Vector4[] _ripples = new Vector4[MaxRipples];
        int _nextRipple;
        MaterialPropertyBlock _mpb;
        MeshRenderer _canvas;
        float _enter;
        Vector2 _enterOrigin = new Vector2(0.5f, 0.5f);

        /// <summary>Builds the painting under parent at a wall position.</summary>
        public static PaintingView Create(Transform parent, ProjectInfo info, Vector3 localPos, Quaternion localRot,
                                          float width, bool compactPlaque)
        {
            var go = new GameObject($"Painting {info.id}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            var view = go.AddComponent<PaintingView>();
            view.Build(info, width, compactPlaque);
            return view;
        }

        void Build(ProjectInfo info, float width, bool compactPlaque)
        {
            Info = info;
            size = new Vector2(width, width / 1.6f);
            _mpb = new MaterialPropertyBlock();

            // Canvas
            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(transform, false);
            canvasGo.AddComponent<MeshFilter>().sharedMesh = Grid();
            _canvas = canvasGo.AddComponent<MeshRenderer>();
            _canvas.sharedMaterial = SharedRippleMaterial();
            _canvas.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            canvasGo.transform.localScale = new Vector3(size.x, size.y, 1);
            ApplyBlock(Placeholder(), Color.Lerp(Palette.Fidelity(info.fidelity), Color.black, 0.35f));

            // Frame (4 bars) + fidelity badge
            Color frame = Palette.Fidelity(info.fidelity);
            float t = 0.07f, d = 0.06f;
            var fr = transform;
            Greybox.Box(fr, "FrameTop", new Vector3(0, size.y / 2 + t / 2, d / 2 - 0.02f), new Vector3(size.x + 2 * t, t, d), frame, false);
            Greybox.Box(fr, "FrameBottom", new Vector3(0, -size.y / 2 - t / 2, d / 2 - 0.02f), new Vector3(size.x + 2 * t, t, d), frame, false);
            Greybox.Box(fr, "FrameLeft", new Vector3(-size.x / 2 - t / 2, 0, d / 2 - 0.02f), new Vector3(t, size.y, d), frame, false);
            Greybox.Box(fr, "FrameRight", new Vector3(size.x / 2 + t / 2, 0, d / 2 - 0.02f), new Vector3(t, size.y, d), frame, false);

            var badge = Greybox.Label(fr, info.fidelity.ToUpperInvariant(),
                new Vector3(size.x / 2 - 0.25f, size.y / 2 + t + 0.06f, -0.03f), Quaternion.identity,
                compactPlaque ? 0.55f : 0.7f, 0.9f, frame, TextAlignmentOptions.Right, 0.15f);
            badge.fontStyle = FontStyles.Bold;

            // Plaque
            float plaqueH = compactPlaque ? 0.40f : 0.72f;   // includes the star bar row
            float plaqueY = -size.y / 2 - t - 0.06f - plaqueH / 2;
            Greybox.Box(fr, "Plaque", new Vector3(0, plaqueY, 0.0f), new Vector3(size.x + 2 * t, plaqueH, 0.03f), Palette.Plaque, false);

            // Plain text, not a glyph: the default TMP font may not include ★.
            string star = info.winner ? "<color=#F2C14E>WINNER</color>  " : "";
            string prize = info.prizes != null && info.prizes.Count > 0 ? $"\n<size=70%><color=#F2C14E>{Escape(info.prizes[0])}</color></size>" : "";
            string body = compactPlaque
                ? $"<b>{star}{Escape(info.title)}</b>\n<size=75%>{Escape(info.device_label)}</size>"
                : $"<b>{star}{Escape(info.title)}</b>{prize}\n<size=72%>{Escape(info.device_label)} · {Palette.FidelityBlurb(info.fidelity)}</size>\n" +
                  $"<size=62%>{Escape(info.synopsis)}</size>";
            const float starRow = 0.1f;
            Greybox.Label(fr, body, new Vector3(0, plaqueY + starRow / 2, -0.02f), Quaternion.identity,
                compactPlaque ? 0.55f : 0.62f, size.x + 0.05f, Palette.TextOnDark, TextAlignmentOptions.TopLeft, plaqueH - starRow - 0.04f);
            // Star bar along the bottom-left of the plaque (CLAUDE.md: 1–5 star ratings on every painting).
            float barLeft = -size.x / 2 - t + 0.04f + 0.075f * 2.5f;
            StarBar.Create(fr, info, new Vector3(barLeft, plaqueY - plaqueH / 2 + starRow / 2 + 0.01f, -0.02f),
                size.x + 2 * t - 0.075f * 5 - 0.12f);

            EnsureCollider();
        }

        static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("<", "&lt;");

        // ---------- ripples ----------

        protected override void OnTouchStart(Vector2 uv) => AddRipple(uv, 1f);
        protected override void OnTouchMove(Vector2 uv) => AddRipple(uv, 0.6f);
        protected override void OnPush(Vector2 uv)
        {
            AddRipple(uv, 1.2f);
            PushedThrough?.Invoke(this, uv);
        }

        public void AddRipple(Vector2 uv, float amplitude)
        {
            _ripples[_nextRipple] = new Vector4(uv.x, uv.y, Time.timeSinceLevelLoad, amplitude);
            _nextRipple = (_nextRipple + 1) % MaxRipples;
            PushBlock();
        }

        /// <summary>0..1 swirl toward origin; driven by the jump-in transition.</summary>
        public void SetEnter(float amount, Vector2 origin)
        {
            _enter = amount;
            _enterOrigin = origin;
            PushBlock();
        }

        // ---------- texture ----------

        public void SetThumbnail(Texture2D tex)
        {
            if (Thumbnail != null && Thumbnail != tex) Destroy(Thumbnail);
            Thumbnail = tex;
            if (tex != null)
            {
                // Center-crop the thumbnail to the fixed canvas aspect (frame and plaque don't move).
                float texAspect = (float)tex.width / Mathf.Max(1, tex.height);
                float canvasAspect = size.x / size.y;
                _crop = texAspect < canvasAspect
                    ? new Vector4(1, texAspect / canvasAspect, 0, (1 - texAspect / canvasAspect) / 2)
                    : new Vector4(canvasAspect / texAspect, 1, (1 - canvasAspect / texAspect) / 2, 0);
                ApplyBlock(tex, Color.white);
            }
        }

        /// <summary>Show a texture this painting doesn't own (e.g. a video RenderTexture).</summary>
        public void ShowExternalTexture(Texture tex)
        {
            _crop = new Vector4(1, 1, 0, 0);
            ApplyBlock(tex, Color.white);
        }

        public void ReleaseThumbnail()
        {
            if (Thumbnail != null) Destroy(Thumbnail);
            Thumbnail = null;
            ThumbnailRequested = false;
            _crop = new Vector4(1, 1, 0, 0);
            ApplyBlock(Placeholder(), Color.Lerp(Palette.Fidelity(Info.fidelity), Color.black, 0.35f));
        }

        Texture _tex;
        Color _tint;
        Vector4 _crop = new Vector4(1, 1, 0, 0);
        static readonly int MainTexStId = Shader.PropertyToID("_MainTex_ST");

        void ApplyBlock(Texture tex, Color tint)
        {
            _tex = tex;
            _tint = tint;
            PushBlock();
        }

        void PushBlock()
        {
            if (_canvas == null) return;
            _mpb.SetTexture(MainTexId, _tex);
            _mpb.SetColor(TintId, _tint);
            _mpb.SetVector(MainTexStId, _crop);
            _mpb.SetFloat(AspectId, size.x / size.y);
            _mpb.SetVectorArray(RipplesId, _ripples);
            _mpb.SetFloat(EnterId, _enter);
            _mpb.SetVector(EnterOriginId, new Vector4(_enterOrigin.x, _enterOrigin.y, 0, 0));
            _canvas.SetPropertyBlock(_mpb);
        }

        void OnDestroy()
        {
            if (Thumbnail != null) Destroy(Thumbnail);
        }

        // ---------- shared assets ----------

        static Material _rippleMat;

        static Material SharedRippleMaterial()
        {
            if (_rippleMat == null)
            {
                var res = Resources.Load<Material>("RHM_Ripple");
                _rippleMat = res != null ? res : new Material(MuseumMaterials.RippleShader);
            }
            return _rippleMat;
        }

        static Texture2D Placeholder()
        {
            if (_placeholder == null)
            {
                _placeholder = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                _placeholder.SetPixels(new[] { Color.white, Color.gray, Color.gray, Color.white });
                _placeholder.filterMode = FilterMode.Bilinear;
                _placeholder.Apply(false, true);
            }
            return _placeholder;
        }

        /// <summary>Unit quad (-0.5..0.5) subdivided so vertex ripples have something to bend.</summary>
        static Mesh Grid()
        {
            if (_sharedGrid != null) return _sharedGrid;
            const int nx = 48, ny = 30;
            var verts = new Vector3[(nx + 1) * (ny + 1)];
            var uvs = new Vector2[verts.Length];
            var normals = new Vector3[verts.Length];
            for (int y = 0; y <= ny; y++)
                for (int x = 0; x <= nx; x++)
                {
                    int i = y * (nx + 1) + x;
                    float u = (float)x / nx, v = (float)y / ny;
                    verts[i] = new Vector3(u - 0.5f, v - 0.5f, 0);
                    uvs[i] = new Vector2(u, v);
                    normals[i] = Vector3.back;   // faces the visitor (-Z)
                }
            var tris = new int[nx * ny * 6];
            int k = 0;
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    int i = y * (nx + 1) + x;
                    // clockwise when seen from -Z (Unity front faces are clockwise)
                    tris[k++] = i; tris[k++] = i + nx + 1; tris[k++] = i + 1;
                    tris[k++] = i + 1; tris[k++] = i + nx + 1; tris[k++] = i + nx + 2;
                }
            _sharedGrid = new Mesh { name = "PaintingGrid", vertices = verts, uv = uvs, normals = normals, triangles = tris };
            _sharedGrid.bounds = new Bounds(Vector3.zero, new Vector3(1, 1, 0.5f));   // room for displacement
            return _sharedGrid;
        }

        public IEnumerator AnimateEnter(Vector2 origin, float duration)
        {
            for (float t = 0; t < duration; t += Time.deltaTime)
            {
                SetEnter(Mathf.SmoothStep(0, 1, t / duration), origin);
                yield return null;
            }
            SetEnter(1, origin);
        }
    }
}
