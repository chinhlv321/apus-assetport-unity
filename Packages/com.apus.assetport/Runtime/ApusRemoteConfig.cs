using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Apus.AssetPort
{
    /// <summary>
    /// Remote config published on the portal (page <b>Remote config</b>). It is a separate JSON file on
    /// the CDN, not part of the Addressables catalog: changing values never needs a content build or upload.
    /// <code>
    /// // In Awake/Start (not a field initializer: Unity APIs are not allowed in MonoBehaviour constructors).
    /// var config = new ApusRemoteConfig("https://cdn.example.com/config/skyrise/prod-xxxx/remote-config.json");
    /// yield return config.Fetch();
    /// int lives = config.GetInt("maxLives", 3);
    /// </code>
    /// The last good file is cached on the device (a file in <see cref="Application.persistentDataPath"/>;
    /// <see cref="PlayerPrefs"/> on WebGL, whose files only reach IndexedDB after an explicit sync): values from
    /// the previous session are available right after construction and stay in use when the device is offline.
    /// A file published earlier than the values in use (stale CDN edge) is ignored. On WebGL the cache shares
    /// the game's PlayerPrefs quota (about 1 MB); keep configs small there.
    /// </summary>
    public sealed class ApusRemoteConfig
    {
        public const int Schema = 1;

        /// <summary>The CDN URL shown on the portal's Remote config page for the env.</summary>
        public string Url { get; }

        /// <summary>Version of the values in use; 0 = nothing loaded (all getters return their fallback).</summary>
        public long Version { get; private set; }

        /// <summary>Error of the last <see cref="Fetch"/>, null when it succeeded.</summary>
        public string LastError { get; private set; }

        /// <summary>Publish time (ISO-8601 UTC) of the values in use; null when nothing is loaded.</summary>
        public string PublishedAt { get; private set; }

        private Dictionary<string, object> _values = new Dictionary<string, object>();
        private readonly Dictionary<string, object> _defaults;
        private readonly string _cacheKey;

        /// <param name="defaults">Optional: getters fall back to these values for keys that are not published.</param>
        public ApusRemoteConfig(string url, ApusRemoteConfigDefaults defaults = null)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("url is required", nameof(url));
            Url = url;
            var errors = new List<string>();
            _defaults = defaults != null ? defaults.ToValues(errors) : new Dictionary<string, object>();
            foreach (var e in errors) Debug.LogWarning("[ApusRemoteConfig] Default ignored: " + e);
            _cacheKey = "apus-remote-config-" + Hash128.Compute(url);
            try
            {
                var cached = ReadCache();
                if (cached != null) Apply(cached);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ApusRemoteConfig] Ignoring unreadable cache: " + e.Message);
            }
        }

        /// <summary>
        /// Downloads the file (coroutine). On failure the current values stay in use,
        /// <see cref="LastError"/> is set and <paramref name="done"/> gets false. The CDN caches the file for up to 60 s.
        /// </summary>
        public IEnumerator Fetch(int timeoutSeconds = 10, Action<bool> done = null)
        {
            using (var req = UnityWebRequest.Get(Url))
            {
                req.timeout = Math.Max(1, timeoutSeconds); // 0 would mean no timeout at all
                yield return req.SendWebRequest();
                bool applied = false;
                string text = null;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    LastError = req.error;
                }
                else
                {
                    try
                    {
                        text = req.downloadHandler.text;
                        applied = Apply(text);
                        LastError = null;
                    }
                    catch (Exception e)
                    {
                        LastError = e.Message;
                    }
                }
                if (LastError != null) Debug.LogWarning("[ApusRemoteConfig] Fetch failed: " + LastError);
                // A cache write failure (disk full, storage quota) only costs offline values next session.
                else if (applied) WriteCache(text);
                done?.Invoke(LastError == null);
            }
        }

        private string CachePath => Path.Combine(Application.persistentDataPath, _cacheKey + ".json");

        private string ReadCache()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return PlayerPrefs.HasKey(_cacheKey) ? PlayerPrefs.GetString(_cacheKey) : null;
#else
            return File.Exists(CachePath) ? File.ReadAllText(CachePath) : null;
#endif
        }

        private void WriteCache(string text)
        {
            try
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                PlayerPrefs.SetString(_cacheKey, text);
                PlayerPrefs.Save();
#else
                File.WriteAllText(CachePath, text);
#endif
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ApusRemoteConfig] Could not cache config: " + e.Message);
            }
        }

        /// <summary>
        /// Parses a config file; false when it is not newer than the values in use and was ignored. Newer means
        /// published later (ISO-8601 UTC strings sort by time): a stale CDN edge serves an earlier file, while a
        /// portal database that was rebuilt (versions start again from 1) still gets through.
        /// </summary>
        private bool Apply(string text)
        {
            var root = Json.AsObject(Json.Parse(text)) ?? throw new FormatException("Remote config is not a JSON object");
            if (Json.GetLong(root, "schema") != Schema) throw new FormatException("Unsupported remote config schema");
            var values = root.TryGetValue("values", out var v) ? Json.AsObject(v) : null;
            if (values == null) throw new FormatException("Remote config has no values");
            var publishedAt = Json.GetString(root, "publishedAt") ?? "";
            if (PublishedAt != null && string.CompareOrdinal(publishedAt, PublishedAt) <= 0) return false;
            Version = Json.GetLong(root, "version");
            PublishedAt = publishedAt;
            _values = values;
            return true;
        }

        /// <summary>Published values win; keys that are not published come from the defaults asset.</summary>
        private Dictionary<string, object> Source(string key) => _values.ContainsKey(key) ? _values : _defaults;

        public bool Has(string key) => _values.ContainsKey(key) || _defaults.ContainsKey(key);

        public IEnumerable<string> Keys => _values.Keys.Union(_defaults.Keys);

        public string GetString(string key, string fallback = null) =>
            Source(key).TryGetValue(key, out var v) && v is string s ? s : fallback;

        public double GetDouble(string key, double fallback = 0) =>
            Source(key).TryGetValue(key, out var v) && v is double d ? d : fallback;

        public float GetFloat(string key, float fallback = 0) => (float)GetDouble(key, fallback);

        public long GetLong(string key, long fallback = 0) => Json.GetLong(Source(key), key, fallback);

        public int GetInt(string key, int fallback = 0)
        {
            var n = GetLong(key, fallback);
            return n >= int.MinValue && n <= int.MaxValue ? (int)n : fallback;
        }

        public bool GetBool(string key, bool fallback = false) =>
            Source(key).TryGetValue(key, out var v) && v is bool b ? b : fallback;

        /// <summary>The value as JSON text (any type), e.g. for <c>JsonUtility.FromJson&lt;T&gt;(config.GetJson("shop"))</c>.</summary>
        public string GetJson(string key, string fallback = null) =>
            Source(key).TryGetValue(key, out var v) ? Json.Serialize(v) : fallback;

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "ApusRemoteConfig(v{0}, {1} keys)", Version, _values.Count);
    }
}
