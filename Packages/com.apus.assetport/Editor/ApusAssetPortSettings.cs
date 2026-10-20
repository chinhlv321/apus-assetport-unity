using System;
using UnityEditor;
using UnityEngine;

namespace Apus.AssetPort
{
    /// <summary>
    /// Per-project settings, saved to <c>ProjectSettings/ApusAssetPortSettings.asset</c> (commit it).
    /// Credentials are never stored here — see <see cref="ApusCredentials"/>.
    /// </summary>
    [FilePath("ProjectSettings/ApusAssetPortSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class ApusAssetPortSettings : ScriptableSingleton<ApusAssetPortSettings>
    {
        [Tooltip("Slug của project trên portal, ví dụ skyrise")]
        public string projectSlug = "";

        [Tooltip("URL portal, ví dụ https://admin.example.com (local: http://127.0.0.1:8787)")]
        public string portalUrl = "";

        [Tooltip("Ghi apus-manifest.json sau mỗi lần build Addressables")]
        public bool generateManifest = true;

        // Inputs of Apus > Setup Addressables… (copied from the portal's project Settings page). Not secret, but the
        // dev/staging envPaths reveal where unreleased content lives: keep them in the game's (private) repo only.
        [Tooltip("CDN base URL, ví dụ https://cdn.example.com")]
        public string cdnBaseUrl = "";

        [Tooltip("envPath của dev trên portal, ví dụ dev-7f3k2q")]
        public string envPathDev = "";

        [Tooltip("envPath của staging trên portal, ví dụ staging-7f3k2q")]
        public string envPathStaging = "";

        public void SaveSettings() => Save(true);
    }

    /// <summary>
    /// Credentials and effective portal URL. EditorPrefs (per machine, keyed by portal URL);
    /// env vars <c>APUS_PORTAL_URL</c>, <c>APUS_CLIENT_ID</c>, <c>APUS_CLIENT_SECRET</c>,
    /// <c>APUS_DEV_EMAIL</c> override them (CI).
    /// </summary>
    public static class ApusCredentials
    {
        const string k_Prefix = "Apus.AssetPort.";

        public static string PortalUrl
        {
            get
            {
                var env = Env("APUS_PORTAL_URL");
                return NormalizeUrl(env ?? ApusAssetPortSettings.instance.portalUrl);
            }
        }

        public static bool PortalUrlFromEnv => Env("APUS_PORTAL_URL") != null;

        public static string ClientId
        {
            get => Env("APUS_CLIENT_ID") ?? Get("ClientId");
            set => Set("ClientId", value);
        }

        public static string ClientSecret
        {
            get => Env("APUS_CLIENT_SECRET") ?? Get("ClientSecret");
            set => Set("ClientSecret", value);
        }

        /// <summary>Only used when the portal is localhost (wrangler dev): sent as <c>X-Dev-Email</c>.</summary>
        public static string DevEmail
        {
            get => Env("APUS_DEV_EMAIL") ?? Get("DevEmail");
            set => Set("DevEmail", value);
        }

        public static bool IsLocalhost(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host.ToLowerInvariant();
            return host == "localhost" || host == "127.0.0.1" || host == "[::1]" || host == "::1" || host.EndsWith(".localhost", StringComparison.Ordinal);
        }

        public static string NormalizeUrl(string url) => (url ?? "").Trim().TrimEnd('/');

        /// <summary>Returns null when everything needed to call the portal is present, else the problem.</summary>
        public static string Problem()
        {
            var url = PortalUrl;
            if (string.IsNullOrEmpty(url)) return "Chưa đặt Portal URL (Project Settings > Apus Asset Port hoặc env APUS_PORTAL_URL).";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                return "Portal URL không hợp lệ: " + url;
            if (IsLocalhost(url) && !string.IsNullOrEmpty(DevEmail)) return null;
            if (uri.Scheme != "https" && !IsLocalhost(url)) return "Portal URL phải dùng https: " + url;
            if (string.IsNullOrEmpty(ClientId) || string.IsNullOrEmpty(ClientSecret))
                return IsLocalhost(url)
                    ? null // wrangler dev falls back to DEV_AUTH_EMAIL
                    : "Thiếu Access service token (Client ID/Secret) — nhập trong cửa sổ Apus > Build & Upload hoặc env APUS_CLIENT_ID / APUS_CLIENT_SECRET.";
            return null;
        }

        // Plaintext in EditorPrefs (registry / plist), per machine, keyed by portal URL + project slug so two
        // projects on the same portal keep their own service tokens.
        static string Key(string name) =>
            k_Prefix + name + "|" + PortalUrl.ToLowerInvariant() + "|" + (ApusAssetPortSettings.instance.projectSlug ?? "").Trim();

        /// <summary>Key used by 1.0.0 (portal URL only); read once as a fallback and copied to the new key.</summary>
        static string LegacyKey(string name) => k_Prefix + name + "|" + PortalUrl.ToLowerInvariant();

        static string Get(string name)
        {
            var key = Key(name);
            if (EditorPrefs.HasKey(key)) return EditorPrefs.GetString(key, "");
            var legacy = LegacyKey(name);
            if (!EditorPrefs.HasKey(legacy)) return "";
            var value = EditorPrefs.GetString(legacy, "");
            EditorPrefs.SetString(key, value);
            return value;
        }

        static void Set(string name, string value) => EditorPrefs.SetString(Key(name), (value ?? "").Trim());

        static string Env(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
    }
}
