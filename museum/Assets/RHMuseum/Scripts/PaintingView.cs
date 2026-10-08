using System;
using System.Collections;
using System.Collections.Generic;
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
        public enum Plaque { Full, Compact, None }

        /// <summary>Every live painting (for perf modes and culling).</summary>
        public static readonly List<PaintingView> All = new List<PaintingView>();

        public ProjectInfo Info { get; private set; }
        public Texture2D Thumbnail { get; private set; }
        public bool WantsThumbnail => !string.IsNullOrEmpty(Info?.thumbnail);
        public bool ThumbnailLoaded => Thumbnail != null;
        public bool ThumbnailRequested { get; set; }

        public static event Action<PaintingView, Vector2> PushedThrough;

        const int MaxRipples = 4;
        // exp(-1.4 * 4.5) ≈ 0.002: a ripple this old is invisible, so the painting can go back to the idle variant.
        const float RippleLifetime = 4.5f;
        static Mesh _sharedGrid, _sharedQuad;
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
        MeshFilter _canvasMesh;
        float _hotUntil = -1;          // ripple variant until this time (Time.timeSinceLevelLoad)
        bool _surfaceActive;
        readonly List<Renderer> _bodyRenderers = new List<Renderer>();
        readonly List<Renderer> _textRenderers = new List<Renderer>();
        bool _rendered = true, _textRendered = true;
        public bool TextRendered => _textRendered;
        float _enter;
        Vector2 _enterOrigin = new Vector2(0.5f, 0.5f);

        /// <summary>Builds the painting under parent at a wall position.</summary>
        public static PaintingView Create(Transform parent, ProjectInfo info, Vector3 localPos, Quaternion localRot,
                                          float width, bool compactPlaque) =>
            Create(parent, info, localPos, localRot, width, compactPlaque ? Plaque.Compact : Plaque.Full);

        public static PaintingView Create(Transform parent, ProjectInfo info, Vector3 localPos, Quaternion localRot,
                                          float width, Plaque plaque)
        {
            var go = new GameObject($"Painting {info.id}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            var view = go.AddComponent<PaintingView>();
            view.Build(info, width, plaque);
            return view;
        }

        void Build(ProjectInfo info, float width, Plaque plaque)
        {
            bool compactPlaque = plaque == Plaque.Compact;
            Info = info;
            size = new Vector2(width, width / 1.6f);
            _mpb = new MaterialPropertyBlock();

            // Canvas
            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(transform, false);
            _canvasMesh = canvasGo.AddComponent<MeshFilter>();
            _canvas = canvasGo.AddComponent<MeshRenderer>();
            UpdateSurface(force: true);
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

            if (plaque == Plaque.None)
            {
                EnsureCollider();
                CacheRenderers();
                return;
            }

            // Plaque
            float plaqueH = compactPlaque ? 0.40f : 0.72f;   // includes the star bar row
            float plaqueY = -size.y / 2 - t - 0.06f - plaqueH / 2;
            Greybox.Box(fr, "Plaque", new Vector3(0, plaqueY, 0.0f), new Vector3(size.x + 2 * t, plaqueH, 0.03f), Palette.Plaque, false);

            string body = PlaqueText(info, compactPlaque);
            const float starRow = 0.1f;
            Greybox.Label(fr, body, new Vector3(0, plaqueY + starRow / 2, -0.02f), Quaternion.identity,
                compactPlaque ? 0.55f : 0.62f, size.x + 0.05f, Palette.TextOnDark, TextAlignmentOptions.TopLeft, plaqueH - starRow - 0.04f);
            // Star bar along the bottom-left of the plaque (CLAUDE.md: 1–5 star ratings on every painting).
            float barLeft = -size.x / 2 - t + 0.04f + 0.075f * 2.5f;
            StarBar.Create(fr, info, new Vector3(barLeft, plaqueY - plaqueH / 2 + starRow / 2 + 0.01f, -0.02f),
                size.x + 2 * t - 0.075f * 5 - 0.12f);

            EnsureCollider();
            CacheRenderers();
        }

        void CacheRenderers()
        {
            _bodyRenderers.Clear();
            _textRenderers.Clear();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                (r.GetComponent<TMP_Text>() != null ? _textRenderers : _bodyRenderers).Add(r);
        }

        /// <summary>Plaque rich text (also used by the theater's lectern card).</summary>
        public static string PlaqueText(ProjectInfo info, bool compact)
        {
            // Plain text, not a glyph: the default TMP font may not include ★.
            string star = info.winner ? "<color=#F2C14E>WINNER</color>  " : "";
            string prize = info.prizes != null && info.prizes.Count > 0 ? $"\n<size=70%><color=#F2C14E>{Escape(info.prizes[0])}</color></size>" : "";
            return compact
                ? $"<b>{star}{Escape(info.title)}</b>\n<size=75%>{Escape(info.device_label)}</size>"
                : $"<b>{star}{Escape(info.title)}</b>{prize}\n<size=72%>{Escape(info.device_label)} · {Palette.FidelityBlurb(info.fidelity)}</size>\n" +
                  $"<size=62%>{Escape(info.synopsis)}</size>";
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
            _hotUntil = Time.timeSinceLevelLoad + RippleLifetime;
            UpdateSurface();
            PushBlock();
        }

        /// <summary>0..1 swirl toward origin; driven by the jump-in transition.</summary>
        public void SetEnter(float amount, Vector2 origin)
        {
            _enter = amount;
            _enterOrigin = origin;
            if (amount > 0) _hotUntil = Mathf.Max(_hotUntil, Time.timeSinceLevelLoad + 0.5f);
            UpdateSurface();
            PushBlock();
        }

        // ---------- idle / ripple surface ----------

        /// <summary>
        /// Idle paintings draw a 4-vertex quad with the cheap shader variant; touched or entered ones switch to
        /// the 1,500-vertex grid with the wave math until their ripples have died out. With the RippleIdle perf
        /// fix off, every painting uses the ripple variant (the old behavior).
        /// </summary>
        void UpdateSurface(bool force = false)
        {
            bool active = !PerfModes.Has(PerfFix.RippleIdle) || _enter > 0 || Time.timeSinceLevelLoad < _hotUntil;
            if (active == _surfaceActive && !force) return;
            _surfaceActive = active;
            _canvasMesh.sharedMesh = active ? Grid() : Quad();
            _canvas.sharedMaterial = SurfaceMaterial(active);
        }

        /// <summary>Called once per frame (MuseumCulling): returns paintings whose ripples have finished to idle.</summary>
        public static void UpdateAllSurfaces()
        {
            for (int i = 0; i < All.Count; i++)
                if (All[i]._surfaceActive) All[i].UpdateSurface();
        }

        /// <summary>After a perf-mode change.</summary>
        public static void RefreshAllSurfaces()
        {
            for (int i = 0; i < All.Count; i++) All[i].UpdateSurface(force: true);
        }

        // ---------- culling ----------

        /// <summary>Show or hide the whole painting and, separately, its text. Only touches renderers that change.</summary>
        public void SetRendered(bool rendered, bool textRendered)
        {
            textRendered &= rendered;
            if (rendered != _rendered)
            {
                _rendered = rendered;
                foreach (var r in _bodyRenderers) if (r != null) r.enabled = rendered;
            }
            if (textRendered != _textRendered)
            {
                _textRendered = textRendered;
                foreach (var r in _textRenderers) if (r != null) r.enabled = textRendered;
            }
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

        TMPro.TextMeshPro _appBadge;

        /// <summary>Install-state line above the frame (top-left), for paintings with a playable port.</summary>
        public void SetAppBadge(string text, Color color)
        {
            if (_appBadge == null)
            {
                _appBadge = Greybox.Label(transform, "", new Vector3(-size.x / 2 + 0.6f, size.y / 2 + 0.13f, -0.03f),
                    Quaternion.identity, 0.42f, 1.2f, color, TextAlignmentOptions.Left, 0.12f);
                _appBadge.fontStyle = FontStyles.Bold;
                _textRenderers.Add(_appBadge.GetComponent<Renderer>());
                _appBadge.GetComponent<Renderer>().enabled = _textRendered;
            }
            _appBadge.text = text;
            _appBadge.color = color;
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

        void Awake() => All.Add(this);

        void OnDestroy()
        {
            All.Remove(this);
            if (Thumbnail != null) Destroy(Thumbnail);
        }

        // ---------- shared assets ----------

        static Material _idleMat, _rippleMat;

        static Material SurfaceMaterial(bool ripple)
        {
            if (_rippleMat == null)
            {
                // Copies, so toggling keywords never edits the asset in Resources.
                var res = Resources.Load<Material>("RHM_Ripple");
                _idleMat = res != null ? new Material(res) : new Material(MuseumMaterials.RippleShader);
                _idleMat.name = "RHM_Ripple (idle)";
                _idleMat.DisableKeyword("RHM_RIPPLE_ON");
                _rippleMat = new Material(_idleMat) { name = "RHM_Ripple (active)" };
                _rippleMat.EnableKeyword("RHM_RIPPLE_ON");
            }
            return ripple ? _rippleMat : _idleMat;
        }

        static Mesh Quad()
        {
            if (_sharedQuad != null) return _sharedQuad;
            _sharedQuad = new Mesh
            {
                name = "PaintingQuad",
                vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) },
                uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) },
                normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back },
                triangles = new[] { 0, 2, 1, 1, 2, 3 },   // clockwise seen from -Z, same as Grid()
            };
            return _sharedQuad;
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
