using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Apus.AssetPort
{
    /// <summary><c>Apus/Build &amp; Upload…</c>: build Addressables for Android/iOS and upload to one draft group. Never publishes.</summary>
    public class BuildAndUploadWindow : EditorWindow
    {
        static readonly string[] k_ModeLabels = { "Content Update (Update a Previous Build)", "Baseline (New Build)" };

        [SerializeField] bool m_Android = true;
        [SerializeField] bool m_IOS = true;
        [SerializeField] bool m_WebGL;
        [SerializeField] int m_Mode;
        [SerializeField] string m_GroupName = "";
        [SerializeField] string m_Note = "";
        [SerializeField] bool m_ShowCredentials = true;
        [SerializeField] Vector2 m_LogScroll;

        string m_ClientId, m_ClientSecret, m_DevEmail, m_CredentialsFor;
        string m_TestResult;
        Task<string> m_TestTask;

        [MenuItem("Apus/Build & Upload…", priority = 0)]
        public static void Open()
        {
            var w = GetWindow<BuildAndUploadWindow>("Apus Build & Upload");
            w.minSize = new Vector2(460, 520);
        }

        [MenuItem("Apus/Project Settings…", priority = 40)]
        static void OpenSettings() => SettingsService.OpenProjectSettings(ApusAssetPortSettingsProvider.Path);

        void OnEnable()
        {
            UploadJob.Changed += Repaint;
            if (string.IsNullOrEmpty(m_GroupName)) m_GroupName = DefaultGroupName();
        }

        void OnDisable() => UploadJob.Changed -= Repaint;

        void Update()
        {
            if (m_TestTask != null && m_TestTask.IsCompleted)
            {
                m_TestResult = m_TestTask.IsFaulted ? "✖ " + m_TestTask.Exception?.GetBaseException().Message : m_TestTask.Result;
                m_TestTask = null;
                Repaint();
            }
            var job = UploadJob.Current;
            if (job != null && job.Running) Repaint();
        }

        void OnGUI()
        {
            var job = UploadJob.Current;
            if (job != null) DrawJob(job);
            else DrawForm();
        }

        // ---- form -------------------------------------------------------------------------

        void DrawForm()
        {
            var settings = ApusAssetPortSettings.instance;
            EditorGUILayout.LabelField("Project", string.IsNullOrEmpty(settings.projectSlug) ? "(chưa đặt)" : settings.projectSlug);
            EditorGUILayout.LabelField("Portal", string.IsNullOrEmpty(ApusCredentials.PortalUrl) ? "(chưa đặt)" : ApusCredentials.PortalUrl);
            EditorGUILayout.LabelField("App version", PlayerSettings.bundleVersion);
            if (GUILayout.Button("Project Settings > Apus Asset Port", EditorStyles.miniButton, GUILayout.Width(220)))
                SettingsService.OpenProjectSettings(ApusAssetPortSettingsProvider.Path);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Platform", EditorStyles.boldLabel);
            m_Android = EditorGUILayout.ToggleLeft("Android", m_Android);
            m_IOS = EditorGUILayout.ToggleLeft("iOS", m_IOS);
            m_WebGL = EditorGUILayout.ToggleLeft("WebGL", m_WebGL);

            EditorGUILayout.Space();
            m_Mode = EditorGUILayout.Popup("Kiểu build", m_Mode, k_ModeLabels);
            if (m_Mode == 1)
                EditorGUILayout.HelpBox("Baseline chỉ dùng khi phát hành bản app mới (xem mục \"Quy trình release\" trong README). Nó ghi đè addressables_content_state.bin — nhớ commit file này.", MessageType.Warning);
            EditorGUILayout.BeginHorizontal();
            m_GroupName = EditorGUILayout.TextField("Tên group", m_GroupName);
            if (GUILayout.Button("↺", GUILayout.Width(24))) m_GroupName = DefaultGroupName();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Ghi chú");
            m_Note = EditorGUILayout.TextArea(m_Note, GUILayout.MinHeight(48));

            EditorGUILayout.Space();
            DrawCredentials();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!m_Android && !m_IOS && !m_WebGL))
            {
                if (GUILayout.Button("Build & Upload", GUILayout.Height(32))) StartJob();
            }
            EditorGUILayout.HelpBox("Upload vào một group nháp rồi chốt group. Không publish — publish trên portal.", MessageType.Info);
        }

        void DrawCredentials()
        {
            var portal = ApusCredentials.PortalUrl;
            // Credentials are stored per portal + project slug: reload when either changes.
            var credentialsFor = portal + "|" + ApusAssetPortSettings.instance.projectSlug;
            if (m_CredentialsFor != credentialsFor)
            {
                m_CredentialsFor = credentialsFor;
                m_ClientId = ApusCredentials.ClientId;
                m_ClientSecret = ApusCredentials.ClientSecret;
                m_DevEmail = ApusCredentials.DevEmail;
            }
            m_ShowCredentials = EditorGUILayout.Foldout(m_ShowCredentials, "Xác thực (lưu dạng plaintext trong EditorPrefs của máy này, theo portal + project)", true);
            if (!m_ShowCredentials) return;
            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            m_ClientId = EditorGUILayout.TextField("Access Client ID", m_ClientId);
            m_ClientSecret = EditorGUILayout.PasswordField("Access Client Secret", m_ClientSecret);
            if (ApusCredentials.IsLocalhost(portal))
                m_DevEmail = EditorGUILayout.TextField(new GUIContent("Dev email", "Chỉ dùng với wrangler dev (localhost): gửi header X-Dev-Email thay cho service token"), m_DevEmail);
            if (EditorGUI.EndChangeCheck())
            {
                ApusCredentials.ClientId = m_ClientId;
                ApusCredentials.ClientSecret = m_ClientSecret;
                ApusCredentials.DevEmail = m_DevEmail;
            }
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15);
            using (new EditorGUI.DisabledScope(m_TestTask != null))
            {
                if (GUILayout.Button("Kiểm tra kết nối", GUILayout.Width(140))) TestConnection();
            }
            if (!string.IsNullOrEmpty(m_TestResult)) EditorGUILayout.LabelField(m_TestResult, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
        }

        void TestConnection()
        {
            var problem = ApusCredentials.Problem();
            if (problem != null)
            {
                m_TestResult = "✖ " + problem;
                return;
            }
            var slug = ApusAssetPortSettings.instance.projectSlug;
            var client = new PortalClient(ApusCredentials.PortalUrl, ApusCredentials.ClientId, ApusCredentials.ClientSecret, ApusCredentials.DevEmail);
            m_TestResult = "Đang kiểm tra…";
            m_TestTask = Task.Run(async () =>
            {
                using (client)
                {
                    var me = await client.GetMe(CancellationToken.None).ConfigureAwait(false);
                    var actor = Json.AsObject(me != null && me.TryGetValue("actor", out var a) ? a : null);
                    var who = Json.GetString(actor, "email") ?? Json.GetString(actor, "name") ?? Json.GetString(actor, "clientId") ?? "?";
                    var projects = me != null && me.TryGetValue("projects", out var p) && p is List<object> list
                        ? list.Select(x => Json.GetString(Json.AsObject(x), "slug")).ToList()
                        : new List<string>();
                    var isAdmin = me != null && me.TryGetValue("isAdmin", out var adm) && adm is bool b && b;
                    if (!isAdmin && !projects.Contains(slug))
                        return "⚠ Đăng nhập là " + who + " nhưng không có quyền trên project '" + slug + "'.";
                    return "✔ OK: " + who;
                }
            });
        }

        void StartJob()
        {
            var platforms = new List<string>();
            if (m_Android) platforms.Add("Android");
            if (m_IOS) platforms.Add("iOS");
            if (m_WebGL) platforms.Add("WebGL");

            var errors = new List<string>();
            var problem = ApusCredentials.Problem();
            if (problem != null) errors.Add(problem);
            if (!ManifestValidator.IsValidSlug(ApusAssetPortSettings.instance.projectSlug)) errors.Add("Chưa đặt Project slug hợp lệ.");
            if (string.IsNullOrWhiteSpace(m_GroupName)) errors.Add("Nhập tên group.");
            foreach (var p in platforms)
            {
                var target = UploadJob.ParseTarget(p);
                if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target))
                    errors.Add("Chưa cài module " + p + " Build Support.");
            }
            if (errors.Count > 0)
            {
                EditorUtility.DisplayDialog("Apus Build & Upload", string.Join("\n", errors), "OK");
                return;
            }

            // Errors block the job (the build would fail at the same check anyway); warnings only ask.
            var checks = ConfigChecker.Run();
            var configErrors = checks.Where(c => c.Status == ConfigChecker.Status.Error).ToList();
            if (configErrors.Count > 0)
            {
                EditorUtility.DisplayDialog("Cấu hình Addressables có lỗi",
                    string.Join("\n", configErrors.Select(c => "• " + c.Title + (string.IsNullOrEmpty(c.Hint) ? "" : "\n   → " + c.Hint))) +
                    "\n\nSửa các lỗi trên rồi chạy lại (Apus > Check Addressables Config).", "OK");
                return;
            }
            var configWarnings = checks.Where(c => c.Status == ConfigChecker.Status.Warn).ToList();
            if (configWarnings.Count > 0 &&
                !EditorUtility.DisplayDialog("Cấu hình Addressables có cảnh báo",
                    string.Join("\n", configWarnings.Select(c => "• " + c.Title)) + "\n\nVẫn tiếp tục?", "Tiếp tục", "Huỷ"))
                return;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var kind = m_Mode == 1 ? PortalConstants.KindBaseline : PortalConstants.KindUpdate;
            // Build the active platform first to save one platform switch.
            var active = EditorUserBuildSettings.activeBuildTarget.ToString();
            platforms = platforms.OrderBy(p => p == active ? 0 : 1).ToList();
            UploadJob.Start(platforms, kind, m_GroupName.Trim(), m_Note.Trim());
        }

        // ---- job --------------------------------------------------------------------------

        void DrawJob(UploadJob.State job)
        {
            EditorGUILayout.LabelField("Group", job.groupName + (job.groupId > 0 ? "  (#" + job.groupId + ")" : ""), EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Platform", string.Join(", ", job.platforms) + "  —  " + job.kind);

            if (job.Running)
            {
                UploadJob.Progress.Read(out var stage, out var current, out var fraction, out var counts);
                var label = job.phase == UploadJob.Phase.Uploading || job.phase == UploadJob.Phase.Completing
                    ? stage + "  " + counts
                    : (job.CurrentPlatform ?? "") + ": " + job.phase;
                var rect = EditorGUILayout.GetControlRect(false, 22);
                EditorGUI.ProgressBar(rect, job.phase == UploadJob.Phase.Uploading ? fraction : 0f, label);
                if (job.phase == UploadJob.Phase.Uploading && !string.IsNullOrEmpty(current))
                    EditorGUILayout.LabelField(current, EditorStyles.miniLabel);
                if (GUILayout.Button("Huỷ")) UploadJob.Cancel();
            }
            else if (job.phase == UploadJob.Phase.Done)
            {
                EditorGUILayout.HelpBox("Xong. Group đã được chốt (uploaded), CHƯA publish.\n" + string.Join("\n", job.summary), MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(job.error ?? "Thất bại.", MessageType.Error);
            }

            if (job.groupId > 0 && GUILayout.Button("Mở group trên portal: " + Uploader.GroupUrl(job.portalUrl, job.groupId)))
                Application.OpenURL(Uploader.GroupUrl(job.portalUrl, job.groupId));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            m_LogScroll = EditorGUILayout.BeginScrollView(m_LogScroll, GUILayout.ExpandHeight(true));
            foreach (var line in job.log) EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndScrollView();

            if (!job.Running && GUILayout.Button("Đóng / build mới"))
            {
                UploadJob.Clear();
                m_GroupName = DefaultGroupName();
            }
        }

        static string DefaultGroupName() =>
            PlayerSettings.bundleVersion + " content " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
