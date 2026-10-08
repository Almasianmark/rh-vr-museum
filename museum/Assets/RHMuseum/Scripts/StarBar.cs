using System.Linq;
using TMPro;
using UnityEngine;

namespace RHMuseum
{
    /// <summary>
    /// Five touchable stars on a painting's plaque. Push a star to rate 1–5.
    /// Shows your own rating (gold) if you've rated, else the museum average (silver), plus "avg · count".
    /// Stars are generated meshes, so they don't depend on font glyphs.
    /// </summary>
    public class StarBar : TouchTarget
    {
        const float StarSize = 0.075f;
        static Mesh _star;
        static readonly Color Gold = new Color(0.95f, 0.76f, 0.30f);
        static readonly Color Silver = new Color(0.78f, 0.80f, 0.84f);
        static readonly Color Empty = new Color(0.30f, 0.31f, 0.35f);

        ProjectInfo _info;
        readonly MeshRenderer[] _stars = new MeshRenderer[5];
        TextMeshPro _label;

        public static StarBar Create(Transform parent, ProjectInfo info, Vector3 localPos, float labelWidth)
        {
            var go = new GameObject("Stars");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var bar = go.AddComponent<StarBar>();
            bar._info = info;
            bar.size = new Vector2(StarSize * 5, StarSize);
            bar.pushDepth = 0.02f;
            for (int i = 0; i < 5; i++)
            {
                var s = new GameObject($"Star{i + 1}");
                s.transform.SetParent(go.transform, false);
                s.transform.localPosition = new Vector3((i - 2) * StarSize, 0, -0.004f);
                s.transform.localScale = Vector3.one * StarSize * 0.92f;
                s.AddComponent<MeshFilter>().sharedMesh = StarMesh();
                bar._stars[i] = s.AddComponent<MeshRenderer>();
                bar._stars[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            bar._label = Greybox.Label(go.transform, "", new Vector3(StarSize * 2.5f + 0.04f + labelWidth / 2, 0, -0.004f),
                Quaternion.identity, 0.42f, labelWidth, Palette.TextOnDark, TextAlignmentOptions.Left, StarSize);
            bar.EnsureCollider();
            bar.Refresh();
            return bar;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            RatingsClient.Changed += Refresh;
            Refresh();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            RatingsClient.Changed -= Refresh;
        }

        protected override void OnPush(Vector2 uv)
        {
            var client = RatingsClient.Instance;
            if (client == null) return;
            int stars = Mathf.Clamp(Mathf.FloorToInt(uv.x * 5) + 1, 1, 5);
            client.Rate(_info.id, stars);
        }

        public void Refresh()
        {
            if (_info == null || _label == null) return;
            var client = RatingsClient.Instance;
            int mine = client != null ? client.MyRating(_info.id) : 0;
            int count = _info.rating_count;
            float avg = _info.avg_stars;
            if (client != null && client.TryGetScore(_info.id, out var s))
            {
                count = s.rating_count;
                avg = s.avg_stars;
            }

            float shown = mine > 0 ? mine : avg;
            Color on = mine > 0 ? Gold : Silver;
            for (int i = 0; i < 5; i++)
                _stars[i].sharedMaterial = MuseumMaterials.Greybox(shown >= i + 0.75f ? on : Empty);

            string stats = count > 0 ? $"{avg:0.0} · {count} rating{(count == 1 ? "" : "s")}" : "Not rated yet";
            _label.text = client == null ? stats : mine > 0 ? $"{stats}  <color=#F2C14E>you: {mine}</color>" : $"{stats}  · touch to rate";
        }

        /// <summary>Flat five-point star in local XY (unit size), facing -Z like the paintings.</summary>
        static Mesh StarMesh()
        {
            if (_star != null) return _star;
            var v = new Vector3[11];
            v[0] = Vector3.zero;
            for (int i = 0; i < 10; i++)
            {
                float r = i % 2 == 0 ? 0.5f : 0.21f;
                float a = Mathf.PI / 2 + i * Mathf.PI / 5;
                v[i + 1] = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0);
            }
            var tris = new int[30];
            for (int i = 0; i < 10; i++)
            {
                // clockwise seen from -Z: center, next, current
                tris[i * 3] = 0;
                tris[i * 3 + 1] = (i + 1) % 10 + 1;
                tris[i * 3 + 2] = i + 1;
            }
            _star = new Mesh { name = "Star", vertices = v, triangles = tris, normals = Enumerable.Repeat(Vector3.back, 11).ToArray() };
            return _star;
        }
    }

    /// <summary>Lobby board: top 10 projects by Bayesian average (needs at least one rating).</summary>
    public class TopRatedBoard : MonoBehaviour
    {
        TextMeshPro _text;
        MuseumDoc _doc;

        public static TopRatedBoard Create(Transform parent, MuseumDoc doc, Vector3 localPos, Quaternion rot)
        {
            var go = new GameObject("Top Rated");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = rot;
            var b = go.AddComponent<TopRatedBoard>();
            b._doc = doc;
            b._text = Greybox.Label(go.transform, "", Vector3.zero, Quaternion.identity, 1.2f, 7f, Palette.Text, TextAlignmentOptions.TopLeft, 3.2f);
            b.Refresh();
            return b;
        }

        void OnEnable() => RatingsClient.Changed += Refresh;
        void OnDisable() => RatingsClient.Changed -= Refresh;

        void Refresh()
        {
            if (_text == null) return;
            var client = RatingsClient.Instance;
            if (client == null)
            {
                _text.text = "<b>Top rated</b>\nRatings are offline in this build.";
                return;
            }
            var top = client.AllScores.Where(s => s.rating_count > 0)
                .OrderByDescending(s => s.bayes_score).ThenByDescending(s => s.rating_count).Take(10).ToList();
            if (top.Count == 0)
            {
                _text.text = "<b>Top rated</b>\nNo ratings yet. Touch the stars under any painting.";
                return;
            }
            var lines = top.Select((s, i) =>
            {
                var p = _doc.Project(s.project_id);
                string title = p != null ? p.title : s.project_id;
                return $"{i + 1}. <b>{title}</b> ({(p != null ? p.year : 0)})  {s.avg_stars:0.0} · {s.rating_count}";
            });
            _text.text = "<b>Top rated</b>  <size=70%>(Bayesian average)</size>\n" + string.Join("\n", lines);
        }
    }
}
