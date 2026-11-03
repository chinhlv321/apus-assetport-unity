using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Apus.AssetPort
{
    public enum ApusRemoteConfigType
    {
        String,
        Number,
        Bool,
        Json,
    }

    /// <summary>
    /// Remote config keys declared in the project with their default values. Export it as JSON
    /// (<b>Apus > Export Remote Config Defaults…</b>) and import that file on the portal's Remote config page;
    /// pass it to <see cref="ApusRemoteConfig"/> so getters fall back to these values when a key is not published.
    /// </summary>
    [CreateAssetMenu(fileName = "ApusRemoteConfigDefaults", menuName = "Apus/Remote Config Defaults")]
    public sealed class ApusRemoteConfigDefaults : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string key;
            public ApusRemoteConfigType type;

            [Tooltip("number: 100 or 0.5 · bool: true/false · json: any JSON value, e.g. {\"isEnabled\":true}")]
            [TextArea(1, 6)]
            public string value;
        }

        public List<Entry> entries = new List<Entry>();

        // Same rules as the portal (shared/src/index.ts, validateRemoteConfigEntries).
        static readonly Regex k_Key = new Regex(@"^[A-Za-z_][A-Za-z0-9_.-]{0,63}\z", RegexOptions.CultureInvariant);
        static readonly string[] k_Reserved = { "__proto__", "constructor", "prototype" };

        /// <summary>Parsed values by key; every entry that cannot be parsed adds a message to <paramref name="errors"/> and is left out.</summary>
        public Dictionary<string, object> ToValues(List<string> errors = null)
        {
            var values = new Dictionary<string, object>();
            foreach (var e in entries)
            {
                var key = e.key ?? "";
                string error = null;
                object value = null;
                if (!k_Key.IsMatch(key) || k_Reserved.Contains(key)) error = "key không hợp lệ ([A-Za-z_][A-Za-z0-9_.-], tối đa 64 ký tự)";
                else if (values.ContainsKey(key)) error = "key trùng";
                else if (!TryParse(e.type, e.value ?? "", out value)) error = "giá trị không khớp kiểu " + TypeName(e.type);
                if (error != null) errors?.Add("'" + key + "': " + error);
                else values[key] = value;
            }
            return values;
        }

        /// <summary>The array format the portal imports: one <c>{"key","type","value"}</c> per line.</summary>
        public string ToJson(List<string> errors)
        {
            var values = ToValues(errors);
            var sb = new StringBuilder("[\n");
            var first = true;
            foreach (var e in entries.Where(x => x.key != null && values.ContainsKey(x.key)))
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  {\"key\":").Append(Json.Quote(e.key))
                  .Append(",\"type\":").Append(Json.Quote(TypeName(e.type)))
                  .Append(",\"value\":").Append(Json.Serialize(values[e.key])).Append('}');
            }
            return sb.Append("\n]\n").ToString();
        }

        public static string TypeName(ApusRemoteConfigType type) => type.ToString().ToLowerInvariant();

        static bool TryParse(ApusRemoteConfigType type, string text, out object value)
        {
            value = null;
            switch (type)
            {
                case ApusRemoteConfigType.String:
                    value = text;
                    return true;
                case ApusRemoteConfigType.Bool:
                    if (text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) value = true;
                    else if (text.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)) value = false;
                    return value != null;
                case ApusRemoteConfigType.Number:
                    if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d) || double.IsInfinity(d)) return false;
                    value = d;
                    return true;
                default:
                    try
                    {
                        value = Json.Parse(text);
                        return true;
                    }
                    catch (FormatException)
                    {
                        return false;
                    }
            }
        }
    }
}
