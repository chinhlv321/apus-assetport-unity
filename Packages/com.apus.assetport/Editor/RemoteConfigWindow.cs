using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Apus.AssetPort
{
    /// <summary>
    /// <b>Apus > Remote Config…</b>: reads the remote config of an env from the portal and publishes edits to
    /// dev / staging. prod is read-only here: publishing it needs the lead role, on the portal.
    /// </summary>
    public class RemoteConfigWindow : EditorWindow
    {
        static readonly string[] k_Envs = { "dev", "staging", "prod" };

        sealed class Loaded
        {
            public string Env;
            public long LiveVersion;
            public List<ApusRemoteConfigDefaults.Entry> Rows = new List<ApusRemoteConfigDefaults.Entry>();
        }

        // Serialized: unpublished edits survive a script recompile (domain reload) while the window is open.
        [SerializeField] int m_Env;
        /// <summary>Env the rows were loaded from; empty until the first load.</summary>
        [SerializeField] string m_LoadedEnv;
        [SerializeField] long m_LiveVersion;
        [SerializeField] List<ApusRemoteConfigDefaults.Entry> m_Rows = new List<ApusRemoteConfigDefaults.Entry>();
        [SerializeField] string m_Note = "";
        string m_Status = "";
        Vector2 m_Scroll;
        Task<Loaded> m_Load;
        Task<long> m_Publish;

        [MenuItem("Apus/Remote Config…", priority = 24)]
        static void Open() => GetWindow<RemoteConfigWindow>("Apus Remote Config");

        bool Busy => m_Load != null || m_Publish != null;

        void OnInspectorUpdate()
        {
            if (!Busy) return;
            Poll();
            Repaint();
        }

        void OnGUI()
        {
            using (new EditorGUI.DisabledScope(Busy))
            {
                EditorGUILayout.BeginHorizontal();
                m_Env = EditorGUILayout.Popup("Env", m_Env, k_Envs);
                if (GUILayout.Button("Tải", GUILayout.Width(80))) Load();
                EditorGUILayout.EndHorizontal();
            }
            if (!string.IsNullOrEmpty(m_Status)) EditorGUILayout.HelpBox(m_Status, MessageType.None);
            if (string.IsNullOrEmpty(m_LoadedEnv)) return;

            EditorGUILayout.LabelField(m_LoadedEnv + ": " + (m_LiveVersion > 0 ? "đang live version " + m_LiveVersion : "chưa publish lần nào"), EditorStyles.boldLabel);
            var readOnly = m_LoadedEnv == "prod";
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            using (new EditorGUI.DisabledScope(Busy || readOnly))
            {
                for (var i = 0; i < m_Rows.Count; i++)
                {
                    var row = m_Rows[i];
                    EditorGUILayout.BeginHorizontal();
                    row.key = EditorGUILayout.TextField(row.key, GUILayout.Width(200));
                    row.type = (ApusRemoteConfigType)EditorGUILayout.EnumPopup(row.type, GUILayout.Width(70));
                    row.value = row.type == ApusRemoteConfigType.Json
                        ? EditorGUILayout.TextArea(row.value, GUILayout.MinHeight(36))
                        : EditorGUILayout.TextField(row.value);
                    var remove = GUILayout.Button("✕", GUILayout.Width(24));
                    EditorGUILayout.EndHorizontal();
                    if (remove) m_Rows.RemoveAt(i--);
                }
                if (GUILayout.Button("Thêm key", GUILayout.Width(100)))
                    m_Rows.Add(new ApusRemoteConfigDefaults.Entry { key = "", type = ApusRemoteConfigType.String, value = "" });
            }
            EditorGUILayout.EndScrollView();

            if (readOnly)
            {
                EditorGUILayout.HelpBox("prod chỉ xem ở đây. Publish prod trên portal (trang Remote config, cần role lead).", MessageType.Info);
                return;
            }
            // Rows belong to the env they were loaded from: publishing them elsewhere must be a deliberate load first.
            var stale = k_Envs[m_Env] != m_LoadedEnv;
            if (stale) EditorGUILayout.HelpBox("Đang chọn env khác với nội dung đã tải (" + m_LoadedEnv + "). Bấm Tải để nạp env đang chọn.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(Busy || stale))
            {
                m_Note = EditorGUILayout.TextField("Ghi chú", m_Note);
                if (GUILayout.Button("Publish " + m_LoadedEnv, GUILayout.Height(28))) Publish();
            }
        }

        static PortalClient NewClient(out string problem)
        {
            problem = ApusCredentials.Problem();
            if (problem == null && !ManifestValidator.IsValidSlug(ApusAssetPortSettings.instance.projectSlug))
                problem = "Chưa đặt Project slug hợp lệ (Project Settings > Apus Asset Port).";
            return problem != null
                ? null
                : new PortalClient(ApusCredentials.PortalUrl, ApusCredentials.ClientId, ApusCredentials.ClientSecret, ApusCredentials.DevEmail);
        }

        void Load()
        {
            var client = NewClient(out var problem);
            if (client == null)
            {
                m_Status = "✖ " + problem;
                return;
            }
            var slug = ApusAssetPortSettings.instance.projectSlug;
            var env = k_Envs[m_Env];
            m_Status = "Đang tải " + env + "…";
            m_Load = Task.Run(async () =>
            {
                using (client)
                    return Parse(env, await client.GetRemoteConfig(slug, env, CancellationToken.None).ConfigureAwait(false));
            });
        }

        /// <summary><c>RemoteConfigResponse</c> → editor rows (the value as the text the user edits).</summary>
        static Loaded Parse(string env, Dictionary<string, object> response)
        {
            var loaded = new Loaded { Env = env, LiveVersion = Json.GetLong(response, "liveVersion") };
            var config = Json.AsObject(response != null && response.TryGetValue("config", out var c) ? c : null);
            var entries = Json.AsArray(config != null && config.TryGetValue("entries", out var e) ? e : null) ?? new List<object>();
            foreach (var item in entries)
            {
                var o = Json.AsObject(item);
                if (o == null) continue;
                Enum.TryParse(Json.GetString(o, "type"), true, out ApusRemoteConfigType type);
                o.TryGetValue("value", out var value);
                loaded.Rows.Add(new ApusRemoteConfigDefaults.Entry
                {
                    key = Json.GetString(o, "key") ?? "",
                    type = type,
                    value = type == ApusRemoteConfigType.String ? value as string ?? "" : Json.Serialize(value),
                });
            }
            return loaded;
        }

        void Publish()
        {
            // Same parsing and key rules as the Remote Config Defaults asset (and the portal).
            var defaults = CreateInstance<ApusRemoteConfigDefaults>();
            defaults.entries = m_Rows;
            var errors = new List<string>();
            var json = defaults.ToJson(errors);
            DestroyImmediate(defaults);
            if (errors.Count > 0)
            {
                EditorUtility.DisplayDialog("Apus Remote Config", "Sửa các key sau rồi publish lại:\n\n" + string.Join("\n", errors.Take(20)), "OK");
                return;
            }
            var env = m_LoadedEnv;
            var baseVersion = m_LiveVersion;
            if (!EditorUtility.DisplayDialog("Publish remote config lên " + env + "?",
                    "Version " + (baseVersion + 1) + " gồm " + m_Rows.Count + " key. Client nhận được sau khoảng 1 phút (cache CDN).", "Publish", "Huỷ"))
                return;
            var client = NewClient(out var problem);
            if (client == null)
            {
                m_Status = "✖ " + problem;
                return;
            }
            var slug = ApusAssetPortSettings.instance.projectSlug;
            var note = m_Note;
            m_Status = "Đang publish " + env + "…";
            m_Publish = Task.Run(async () =>
            {
                using (client)
                    return await client.PublishRemoteConfig(slug, env, baseVersion, json, note, CancellationToken.None).ConfigureAwait(false);
            });
        }

        void Poll()
        {
            if (m_Load != null && m_Load.IsCompleted)
            {
                var task = m_Load;
                m_Load = null;
                if (Failed(task)) return;
                m_LoadedEnv = task.Result.Env;
                m_LiveVersion = task.Result.LiveVersion;
                m_Rows = task.Result.Rows;
                m_Note = "";
                m_Status = "";
                GUI.FocusControl(null); // a focused text field would keep showing its old text
            }
            if (m_Publish != null && m_Publish.IsCompleted)
            {
                var task = m_Publish;
                m_Publish = null;
                if (Failed(task)) return;
                m_LiveVersion = task.Result;
                m_Note = "";
                m_Status = "✔ Đã publish version " + task.Result + " lên " + m_LoadedEnv + ".";
            }
        }

        bool Failed(Task task)
        {
            if (!task.IsFaulted && !task.IsCanceled) return false;
            var e = task.Exception?.GetBaseException();
            m_Status = "✖ " + (e?.Message ?? "đã huỷ") +
                       (e is PortalException p && p.Status == 409 ? "\nBấm Tải để lấy version mới nhất (nội dung đang sửa sẽ mất)." : "");
            return true;
        }
    }
}
