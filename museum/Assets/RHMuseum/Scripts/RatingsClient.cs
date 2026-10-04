using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace RHMuseum
{
    /// <summary>
    /// Supabase ratings (build-order step 3) over plain REST: no SDK, works on Quest.
    ///   auth : anonymous sign-in, refresh token kept in PlayerPrefs, so each headset is one stable voter
    ///   read : rpc/project_scores (Bayesian average + counts), ratings?select=... (own rows via RLS)
    ///   write: rpc/rate_project; failed writes stay queued and retry on the next refresh
    /// </summary>
    public class RatingsClient : MonoBehaviour
    {
        public static RatingsClient Instance { get; private set; }
        public static event Action Changed;

        public bool Ready { get; private set; }
        public string UserId => _session?.user?.id;

        string _url, _anonKey;
        Session _session;
        float _refreshEvery = 60f;
        readonly Dictionary<string, Score> _scores = new Dictionary<string, Score>();
        readonly Dictionary<string, int> _mine = new Dictionary<string, int>();
        readonly Dictionary<string, int> _pending = new Dictionary<string, int>();
        bool _syncing, _kick;

        const string PrefRefresh = "rhm.supabase.refresh_token";
        const string PrefPending = "rhm.supabase.pending";

        [Serializable] class User { public string id; }
        [Serializable] class Session { public string access_token; public string refresh_token; public long expires_at; public int expires_in; public User user; }
        [Serializable] public class Score { public string project_id; public int rating_count; public float avg_stars; public float bayes_score; public int stars; }
        [Serializable] class ScoreList { public List<Score> items = new List<Score>(); }
        [Serializable] class PendingList { public List<Score> items = new List<Score>(); }

        public static RatingsClient Create(string url, string anonKey)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(anonKey)) return null;
            var go = new GameObject("Ratings");
            DontDestroyOnLoad(go);
            var c = go.AddComponent<RatingsClient>();
            c._url = url.TrimEnd('/');
            c._anonKey = anonKey;
            Instance = c;
            return c;
        }

        void Start()
        {
            LoadPending();
            StartCoroutine(Loop());
        }

        // ------------------------------------------------------------ queries used by the UI

        public int MyRating(string projectId) =>
            _pending.TryGetValue(projectId, out var p) ? p : _mine.TryGetValue(projectId, out var m) ? m : 0;

        public bool TryGetScore(string projectId, out Score s) => _scores.TryGetValue(projectId, out s);

        public IEnumerable<Score> AllScores => _scores.Values;

        public void Rate(string projectId, int stars)
        {
            stars = Mathf.Clamp(stars, 1, 5);
            _pending[projectId] = stars;
            SavePending();
            Changed?.Invoke();            // optimistic: the bar shows your stars immediately
            _kick = true;                 // Loop syncs within ~1 s
        }

        // ------------------------------------------------------------ loop

        IEnumerator Loop()
        {
            while (true)
            {
                yield return Sync();
                var tick = new WaitForSeconds(1f);
                for (float t = 0; t < _refreshEvery && !_kick; t += 1f) yield return tick;
                _kick = false;
            }
        }

        IEnumerator Sync()
        {
            if (_syncing) yield break;
            _syncing = true;
            yield return EnsureSession();
            if (_session != null)
            {
                foreach (var kv in new List<KeyValuePair<string, int>>(_pending))
                {
                    bool ok = false;
                    yield return Rpc("rate_project", $"{{\"p_project_id\":{Quote(kv.Key)},\"p_stars\":{kv.Value}}}", (code, _) => ok = code < 300);
                    if (ok)
                    {
                        _mine[kv.Key] = kv.Value;
                        _pending.Remove(kv.Key);
                    }
                }
                SavePending();
                yield return Get("/rest/v1/ratings?select=project_id,stars", body =>
                {
                    foreach (var s in ParseList(body)) _mine[s.project_id] = s.stars;
                });
            }
            yield return Rpc("project_scores", "{}", (code, body) =>
            {
                if (code >= 300) return;
                _scores.Clear();
                foreach (var s in ParseList(body)) _scores[s.project_id] = s;
                Ready = true;
            });
            _syncing = false;
            Changed?.Invoke();
        }

        // ------------------------------------------------------------ auth

        IEnumerator EnsureSession()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_session != null && _session.expires_at - 60 > now) yield break;

            string refresh = _session?.refresh_token ?? PlayerPrefs.GetString(PrefRefresh, "");
            if (!string.IsNullOrEmpty(refresh))
            {
                yield return Auth("/auth/v1/token?grant_type=refresh_token", $"{{\"refresh_token\":{Quote(refresh)}}}");
                if (_session != null) yield break;
            }
            // First launch on this headset (or refresh token revoked): new anonymous user.
            yield return Auth("/auth/v1/signup", "{\"data\":{}}");
            if (_session == null) Debug.LogWarning("[RHMuseum] ratings: anonymous sign-in failed (enabled in Supabase Auth settings?)");
        }

        IEnumerator Auth(string path, string json)
        {
            using (var req = Request("POST", path, json, useSession: false))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _session = null;
                    yield break;
                }
                var s = JsonUtility.FromJson<Session>(req.downloadHandler.text);
                if (s == null || string.IsNullOrEmpty(s.access_token))
                {
                    _session = null;
                    yield break;
                }
                if (s.expires_at == 0) s.expires_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(60, s.expires_in);
                _session = s;
                PlayerPrefs.SetString(PrefRefresh, s.refresh_token);   // rotates on every refresh
                PlayerPrefs.Save();
            }
        }

        // ------------------------------------------------------------ http

        UnityWebRequest Request(string method, string path, string json, bool useSession = true)
        {
            var req = new UnityWebRequest(_url + path, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = 10 };
            if (json != null) req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", _anonKey);
            req.SetRequestHeader("Authorization", "Bearer " + (useSession && _session != null ? _session.access_token : _anonKey));
            return req;
        }

        IEnumerator Rpc(string fn, string json, Action<long, string> done)
        {
            using (var req = Request("POST", "/rest/v1/rpc/" + fn, json))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                    Debug.LogWarning($"[RHMuseum] ratings rpc {fn}: {req.responseCode} {req.error}");
                done(req.responseCode == 0 ? 599 : req.responseCode, req.downloadHandler.text);
            }
        }

        IEnumerator Get(string path, Action<string> done)
        {
            using (var req = Request("GET", path, null))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) done(req.downloadHandler.text);
            }
        }

        // JsonUtility can't read top-level arrays or nulls in numeric fields; wrap and zero them.
        static List<Score> ParseList(string json)
        {
            if (string.IsNullOrEmpty(json) || !json.TrimStart().StartsWith("[")) return new List<Score>();
            json = Regex.Replace(json, @":\s*null", ":0");
            return JsonUtility.FromJson<ScoreList>("{\"items\":" + json + "}").items;
        }

        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        void LoadPending()
        {
            var json = PlayerPrefs.GetString(PrefPending, "");
            if (string.IsNullOrEmpty(json)) return;
            foreach (var s in JsonUtility.FromJson<PendingList>(json).items) _pending[s.project_id] = s.stars;
        }

        void SavePending()
        {
            var list = new PendingList();
            foreach (var kv in _pending) list.items.Add(new Score { project_id = kv.Key, stars = kv.Value });
            PlayerPrefs.SetString(PrefPending, JsonUtility.ToJson(list));
            PlayerPrefs.Save();
        }
    }
}
