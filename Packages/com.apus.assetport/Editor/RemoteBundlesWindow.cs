using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace Apus.AssetPort
{
    /// <summary>
    /// Lists the bundles of the last build in ServerData/[BuildTarget] (from apus-manifest.json), grouped by
    /// Addressables group, and checks with HEAD requests whether each one is on the CDN (Remote.LoadPath).
    /// </summary>
    public class RemoteBundlesWindow : EditorWindow
    {
        const int PingConcurrency = 8;
        static readonly HttpClient k_Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        class Row
        {
            public string Name;
            public long Size;
            public string Url;
            public string Status; // null = not pinged yet
            public bool Ok;
        }

        string m_Info;
        string m_Error;
        string m_LoadPath;
        readonly List<KeyValuePair<string, List<Row>>> m_Groups = new List<KeyValuePair<string, List<Row>>>();
        Vector2 m_Scroll;
        int m_Generation; // bumped on reload so pings of an older list are dropped
        int m_Pinged;

        [MenuItem("Apus/Remote Bundles…", priority = 22)]
        public static void Open() => GetWindow<RemoteBundlesWindow>("Remote Bundles").Reload();

        void OnEnable()
        {
            if (m_Groups.Count == 0 && m_Error == null) Reload();
        }

        IEnumerable<Row> AllRows => m_Groups.SelectMany(g => g.Value);

        void Reload()
        {
            m_Generation++;
            m_Groups.Clear();
            m_Error = null;
            m_Pinged = 0;
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                m_Error = "Chưa có AddressableAssetSettings.";
                return;
            }
            var profiles = settings.profileSettings;
            var profileId = settings.activeProfileId;
            m_LoadPath = profiles.EvaluateString(profileId, profiles.GetValueByName(profileId, AddressablesSetup.RemoteLoadPathVar) ?? "").TrimEnd('/');
            var dir = ManifestWriter.RemoteBuildDir();
            var manifestPath = Path.Combine(dir, PortalConstants.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                m_Error = "Không có " + manifestPath + ". Build Addressables cho " + EditorUserBuildSettings.activeBuildTarget + " trước.";
                return;
            }
            ApusManifest manifest;
            try
            {
                manifest = ApusManifest.FromJson(File.ReadAllText(manifestPath));
            }
            catch (Exception e)
            {
                m_Error = "Không đọc được " + manifestPath + ": " + e.Message;
                return;
            }
            m_Info = "Build lúc " + manifest.builtAt + " · profile " + profiles.GetProfileName(profileId) +
                     "\nLink: " + m_LoadPath + "/\nFile local: " + dir;

            // shortcut: Addressables names bundles "<group lowercased, no spaces>_..."; matched by prefix
            // (longest group name first). Custom bundle naming lands in "(không rõ group)".
            var groups = settings.groups.Where(g => g != null)
                .Select(g => (g.Name, Prefix: g.Name.ToLowerInvariant().Replace(" ", "") + "_"))
                .OrderByDescending(g => g.Prefix.Length).ToList();
            var byGroup = new Dictionary<string, List<Row>>();
            foreach (var f in manifest.files.Where(f => !PortalConstants.IsCatalogFile(f.name)).OrderBy(f => f.name, StringComparer.Ordinal))
            {
                var group = groups.FirstOrDefault(g => f.name.StartsWith(g.Prefix, StringComparison.Ordinal)).Name ?? "(không rõ group)";
                if (!byGroup.TryGetValue(group, out var rows)) byGroup[group] = rows = new List<Row>();
                rows.Add(new Row { Name = f.name, Size = f.size, Url = m_LoadPath + "/" + Uri.EscapeDataString(f.name) });
            }
            m_Groups.AddRange(byGroup.OrderBy(g => g.Key, StringComparer.Ordinal));
        }

        async void PingAll()
        {
            var gen = m_Generation;
            var rows = AllRows.ToList();
            foreach (var r in rows) r.Status = null;
            m_Pinged = 0;
            using (var gate = new SemaphoreSlim(PingConcurrency))
            {
                await Task.WhenAll(rows.Select(async r =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var (status, ok) = await Ping(r);
                        if (gen != m_Generation) return;
                        r.Status = status;
                        r.Ok = ok;
                        m_Pinged++;
                        Repaint();
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }
        }

        static async Task<(string, bool)> Ping(Row r)
        {
            try
            {
                using (var res = await k_Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, r.Url)))
                {
                    if (!res.IsSuccessStatusCode) return ("HTTP " + (int)res.StatusCode, false);
                    var len = res.Content.Headers.ContentLength;
                    if (len.HasValue && len.Value != r.Size) return ("Khác dung lượng: CDN " + Mb(len.Value), false);
                    return ("OK", true);
                }
            }
            catch (Exception e)
            {
                return (e is TaskCanceledException ? "Timeout" : "Lỗi mạng: " + e.Message, false);
            }
        }

        static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Đọc lại build gần nhất")) Reload();
                using (new EditorGUI.DisabledScope(m_Groups.Count == 0 || string.IsNullOrEmpty(m_LoadPath)))
                    if (GUILayout.Button("Ping CDN")) PingAll();
            }
            if (m_Error != null)
            {
                EditorGUILayout.HelpBox(m_Error, MessageType.Warning);
                return;
            }
            EditorGUILayout.HelpBox(m_Info, MessageType.None);

            var rows = AllRows.ToList();
            var ok = rows.Where(r => r.Ok).ToList();
            EditorGUILayout.LabelField(
                "Có trên CDN: " + ok.Count + "/" + rows.Count + " file (" + Mb(ok.Sum(r => r.Size)) + "/" + Mb(rows.Sum(r => r.Size)) + ")" +
                (m_Pinged > 0 ? "  ·  đã ping " + m_Pinged + "/" + rows.Count : "  ·  chưa ping"),
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Bấm tên file để copy URL.", EditorStyles.miniLabel);

            var okStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.35f, 0.75f, 0.4f) } };
            var badStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.85f, 0.35f, 0.3f) } };
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            foreach (var g in m_Groups)
            {
                GUILayout.Space(6);
                EditorGUILayout.LabelField(g.Key + "  (" + g.Value.Count(r => r.Ok) + "/" + g.Value.Count + ")", EditorStyles.boldLabel);
                foreach (var r in g.Value)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var status = (r.Status ?? "chưa ping") + " · local " + Mb(r.Size);
                        GUILayout.Label(status, r.Status == null ? EditorStyles.label : r.Ok ? okStyle : badStyle, GUILayout.Width(260));
                        if (EditorGUILayout.LinkButton(r.Name))
                        {
                            EditorGUIUtility.systemCopyBuffer = r.Url;
                            ShowNotification(new GUIContent("Đã copy URL"));
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
