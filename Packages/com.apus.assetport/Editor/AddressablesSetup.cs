using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Apus.AssetPort
{
    /// <summary>Values from the portal's project Settings page ("Cấu hình nhanh" JSON).</summary>
    public class PortalSetupConfig
    {
        public string Project = "";
        public string PortalUrl = "";
        public string CdnBaseUrl = "";
        public string EnvPathDev = "";
        public string EnvPathStaging = "";

        public const string EnvPathProd = "prod";

        static readonly Regex k_EnvPath = new Regex(@"^[a-z0-9][a-z0-9-]{0,39}\z", RegexOptions.CultureInvariant);

        public static PortalSetupConfig FromSettings(ApusAssetPortSettings s) => new PortalSetupConfig
        {
            Project = s.projectSlug ?? "",
            PortalUrl = s.portalUrl ?? "",
            CdnBaseUrl = s.cdnBaseUrl ?? "",
            EnvPathDev = s.envPathDev ?? "",
            EnvPathStaging = s.envPathStaging ?? "",
        };

        /// <summary>Parses the JSON copied from the portal. Throws <see cref="FormatException"/> with a readable message.</summary>
        public static PortalSetupConfig Parse(string json)
        {
            object root;
            try
            {
                root = Json.Parse(json ?? "");
            }
            catch (Exception e)
            {
                throw new FormatException("Không đọc được JSON: " + e.Message);
            }
            var o = Json.AsObject(root) ?? throw new FormatException("JSON phải là một object { … } copy từ trang Settings của portal.");
            var envPaths = Json.AsObject(o.TryGetValue("envPaths", out var ep) ? ep : null) ?? throw new FormatException("Thiếu envPaths.");
            return new PortalSetupConfig
            {
                Project = Json.GetString(o, "project") ?? "",
                PortalUrl = Json.GetString(o, "portalUrl") ?? "",
                CdnBaseUrl = Json.GetString(o, "cdnBaseUrl") ?? "",
                EnvPathDev = Json.GetString(envPaths, "dev") ?? "",
                EnvPathStaging = Json.GetString(envPaths, "staging") ?? "",
            };
        }

        public string EnvPath(string env) => env == "dev" ? EnvPathDev : env == "staging" ? EnvPathStaging : EnvPathProd;

        public string CdnBase => (CdnBaseUrl ?? "").Trim().TrimEnd('/');

        public string BundleLoadPath => CdnBase + "/bundles/" + Project + "/[BuildTarget]";

        public string CatalogLoadPath(string env) =>
            CdnBase + "/live/" + Project + "/" + EnvPath(env) + "/[BuildTarget]/[UnityEngine.Application.version]";

        /// <summary>Problems that block Apply; empty means OK.</summary>
        public List<string> Validate()
        {
            var errors = new List<string>();
            if (!ManifestValidator.IsValidSlug(Project)) errors.Add("Project slug không hợp lệ: '" + Project + "'.");
            // Usually https://cdn.<domain>; a path is allowed for the local dev CDN (http://127.0.0.1:8787/__dev_cdn).
            if (!Uri.TryCreate(CdnBase, UriKind.Absolute, out var cdn) || (cdn.Scheme != "https" && cdn.Scheme != "http") || !string.IsNullOrEmpty(cdn.Query))
                errors.Add("CDN base URL phải có dạng https://cdn.<domain>: '" + CdnBaseUrl + "'.");
            if (!k_EnvPath.IsMatch(EnvPathDev ?? "")) errors.Add("envPath dev không hợp lệ: '" + EnvPathDev + "'.");
            if (!k_EnvPath.IsMatch(EnvPathStaging ?? "")) errors.Add("envPath staging không hợp lệ: '" + EnvPathStaging + "'.");
            if (!string.IsNullOrEmpty(PortalUrl) && !Uri.TryCreate(PortalUrl, UriKind.Absolute, out _)) errors.Add("Portal URL không hợp lệ: '" + PortalUrl + "'.");
            return errors;
        }

        /// <summary>Non-blocking remarks shown before Apply.</summary>
        public List<string> Warnings()
        {
            var warnings = new List<string>();
            if (CdnBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !ApusCredentials.IsLocalhost(CdnBase))
                warnings.Add("CDN dùng http: iOS chặn http (App Transport Security). Dùng https cho bản build thật.");
            if (EnvPathDev == "dev" || EnvPathStaging == "staging")
                warnings.Add("envPath dev/staging không có hậu tố ngẫu nhiên: người ngoài có thể đoán path và tải content chưa phát hành.");
            return warnings;
        }
    }

    /// <summary>
    /// Applies the required Addressables setup (README "Cấu hình Addressables"): 3 profiles dev/staging/prod,
    /// remote catalog paths, Player Version Override, update restrictions, and optionally the group rules.
    /// Idempotent: running it again only rewrites the values it owns.
    /// </summary>
    public static class AddressablesSetup
    {
        public const string CatalogLoadPathVar = "Apus.CatalogLoadPath";
        public const string RemoteBuildPathVar = "Remote.BuildPath";
        public const string RemoteLoadPathVar = "Remote.LoadPath";
        public const string RemoteBuildPathValue = "ServerData/[BuildTarget]";
        public static readonly string[] Envs = { "dev", "staging", "prod" };

        public struct Options
        {
            /// <summary>Profile left active after Apply (Editor play mode and player builds use it).</summary>
            public string ActiveProfile;
            /// <summary>Remote groups → Append Hash + Remote.BuildPath; local groups → Prevent Updates.</summary>
            public bool FixGroups;
            /// <summary>
            /// "Only update catalogs manually": Addressables.InitializeAsync keeps the cached catalog and the game decides
            /// when to call CheckForCatalogUpdates/UpdateCatalogs (shows progress, handles offline). Off = Addressables
            /// silently switches catalog during init and CheckForCatalogUpdates then finds nothing.
            /// </summary>
            public bool ManualCatalogUpdate;
        }

        /// <summary>Applies <paramref name="config"/>; returns a log of what changed. Throws on invalid config.</summary>
        public static List<string> Apply(PortalSetupConfig config, Options options)
        {
            var errors = config.Validate();
            if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));

            var log = new List<string>();
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);

            var apus = ApusAssetPortSettings.instance;
            apus.projectSlug = config.Project;
            if (!string.IsNullOrEmpty(config.PortalUrl)) apus.portalUrl = ApusCredentials.NormalizeUrl(config.PortalUrl);
            apus.cdnBaseUrl = config.CdnBase;
            apus.envPathDev = config.EnvPathDev;
            apus.envPathStaging = config.EnvPathStaging;
            apus.SaveSettings();
            log.Add("Project Settings > Apus Asset Port: slug " + config.Project + (string.IsNullOrEmpty(config.PortalUrl) ? "" : ", portal " + apus.portalUrl));

            ApplyProfiles(settings, config, string.IsNullOrEmpty(options.ActiveProfile) ? "staging" : options.ActiveProfile, log);
            ApplyCatalogSettings(settings, log);
            settings.DisableCatalogUpdateOnStartup = options.ManualCatalogUpdate;
            log.Add("Only update catalogs manually = " + (options.ManualCatalogUpdate ? "bật (game tự gọi CheckForCatalogUpdates)" : "tắt (Addressables tự cập nhật lúc khởi động)"));
            if (options.FixGroups) ApplyGroups(settings, log);

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return log;
        }

        static void ApplyProfiles(AddressableAssetSettings settings, PortalSetupConfig config, string active, List<string> log)
        {
            var profiles = settings.profileSettings;
            var names = profiles.GetVariableNames();
            foreach (var v in new[] { RemoteBuildPathVar, RemoteLoadPathVar, CatalogLoadPathVar })
            {
                if (names.Contains(v)) continue;
                profiles.CreateValue(v, "");
                log.Add("Tạo biến profile " + v);
            }

            var copyFrom = settings.activeProfileId;
            foreach (var env in Envs)
            {
                var id = profiles.GetProfileId(env);
                if (string.IsNullOrEmpty(id))
                {
                    id = profiles.AddProfile(env, copyFrom);
                    log.Add("Tạo profile " + env);
                }
                profiles.SetValue(id, RemoteBuildPathVar, RemoteBuildPathValue);
                profiles.SetValue(id, RemoteLoadPathVar, config.BundleLoadPath);
                profiles.SetValue(id, CatalogLoadPathVar, config.CatalogLoadPath(env));
                log.Add("Profile " + env + ": catalog " + config.CatalogLoadPath(env));
            }

            var activeId = profiles.GetProfileId(active);
            if (!string.IsNullOrEmpty(activeId))
            {
                settings.activeProfileId = activeId;
                log.Add("Profile đang dùng: " + active);
            }
        }

        static void ApplyCatalogSettings(AddressableAssetSettings settings, List<string> log)
        {
            settings.BuildRemoteCatalog = true;
            settings.RemoteCatalogBuildPath.SetVariableByName(settings, RemoteBuildPathVar);
            settings.RemoteCatalogLoadPath.SetVariableByName(settings, CatalogLoadPathVar);
            settings.OverridePlayerVersion = "content";
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.BuildWithPlayer;
            settings.CheckForContentUpdateRestrictionsOption = CheckForContentUpdateRestrictionsOptions.FailBuild;
            settings.ContentStateBuildPath = ""; // default: Assets/AddressableAssetsData/<platform>, commit it
            log.Add("Remote catalog: bật, Build Path = " + RemoteBuildPathVar + ", Load Path = " + CatalogLoadPathVar);
            log.Add("Player Version Override = content, Build Addressables on Player Build = bật, Check for Update Issues = Fail Build");
        }

        static void ApplyGroups(AddressableAssetSettings settings, List<string> log)
        {
            foreach (var group in settings.groups)
            {
                if (group == null || group.ReadOnly) continue; // ReadOnly: the built-in "Built In Data" group
                var bundled = group.GetSchema<BundledAssetGroupSchema>();
                if (bundled == null) continue;
                if (IsRemote(settings, bundled))
                {
                    var changed = false;
                    if (bundled.BundleNaming != BundledAssetGroupSchema.BundleNamingStyle.AppendHash)
                    {
                        bundled.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.AppendHash;
                        changed = true;
                    }
                    if (bundled.BuildPath.GetName(settings) != RemoteBuildPathVar)
                    {
                        bundled.BuildPath.SetVariableByName(settings, RemoteBuildPathVar);
                        changed = true;
                    }
                    if (bundled.LoadPath.GetName(settings) != RemoteLoadPathVar)
                    {
                        bundled.LoadPath.SetVariableByName(settings, RemoteLoadPathVar);
                        changed = true;
                    }
                    if (changed)
                    {
                        EditorUtility.SetDirty(bundled);
                        log.Add("Group remote " + group.Name + ": Append Hash, " + RemoteBuildPathVar + "/" + RemoteLoadPathVar);
                    }
                }
                else
                {
                    var cu = group.GetSchema<ContentUpdateGroupSchema>() ?? group.AddSchema<ContentUpdateGroupSchema>();
                    if (!cu.StaticContent)
                    {
                        cu.StaticContent = true;
                        // Schemas are separate assets: dirty them or the change is lost on save.
                        EditorUtility.SetDirty(cu);
                        log.Add("Group local " + group.Name + ": bật Prevent Updates");
                    }
                }
                EditorUtility.SetDirty(group);
            }
        }

        /// <summary>A group is remote when its load path points at a URL (or at the Remote.LoadPath variable).</summary>
        static bool IsRemote(AddressableAssetSettings settings, BundledAssetGroupSchema schema)
        {
            if (schema.LoadPath.GetName(settings) == RemoteLoadPathVar) return true;
            var load = schema.LoadPath.GetValue(settings) ?? "";
            return load.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || load.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Apus > Setup Addressables…: paste the portal JSON (or fill the fields) and apply.</summary>
    public class AddressablesSetupWindow : EditorWindow
    {
        PortalSetupConfig m_Config;
        string m_Paste = "";
        string m_PasteError;
        bool m_FixGroups = true;
        bool m_ManualCatalogUpdate = true;
        int m_ActiveProfile = 1; // staging
        List<string> m_Log;
        Vector2 m_Scroll;

        [MenuItem("Apus/Setup Addressables…", priority = 10)]
        public static void Open()
        {
            var w = GetWindow<AddressablesSetupWindow>(true, "Apus · Setup Addressables");
            w.minSize = new Vector2(560, 520);
            w.Show();
        }

        void OnEnable() => m_Config = PortalSetupConfig.FromSettings(ApusAssetPortSettings.instance);

        void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            EditorGUILayout.HelpBox(
                "1. Mở portal > Project > Settings, bấm Copy ở khung \"Cấu hình nhanh cho Unity\".\n" +
                "2. Dán vào ô dưới, bấm \"Đọc cấu hình\" (hoặc nhập tay từng ô).\n" +
                "3. Bấm Apply: tạo/cập nhật 3 profile dev, staging, prod và cấu hình remote catalog theo chuẩn của portal.",
                MessageType.Info);

            EditorGUILayout.LabelField("Dán cấu hình từ portal", EditorStyles.boldLabel);
            m_Paste = EditorGUILayout.TextArea(m_Paste, GUILayout.MinHeight(70));
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_Paste)))
            {
                if (GUILayout.Button("Đọc cấu hình"))
                {
                    try
                    {
                        m_Config = PortalSetupConfig.Parse(m_Paste);
                        m_PasteError = null;
                        GUI.FocusControl(null);
                    }
                    catch (FormatException e)
                    {
                        m_PasteError = e.Message;
                    }
                }
            }
            if (m_PasteError != null) EditorGUILayout.HelpBox(m_PasteError, MessageType.Error);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Giá trị", EditorStyles.boldLabel);
            EditorGUIUtility.labelWidth = 140;
            m_Config.Project = EditorGUILayout.TextField("Project slug", m_Config.Project).Trim();
            m_Config.PortalUrl = EditorGUILayout.TextField("Portal URL", m_Config.PortalUrl).Trim();
            m_Config.CdnBaseUrl = EditorGUILayout.TextField("CDN base URL", m_Config.CdnBaseUrl).Trim();
            m_Config.EnvPathDev = EditorGUILayout.TextField("envPath dev", m_Config.EnvPathDev).Trim();
            m_Config.EnvPathStaging = EditorGUILayout.TextField("envPath staging", m_Config.EnvPathStaging).Trim();
            using (new EditorGUI.DisabledScope(true)) EditorGUILayout.TextField("envPath prod", PortalSetupConfig.EnvPathProd);

            EditorGUILayout.Space();
            m_ActiveProfile = EditorGUILayout.Popup(new GUIContent("Profile đang dùng", "Profile dùng khi build player. Bản QA: staging; bản store: prod."), m_ActiveProfile, AddressablesSetup.Envs);
            m_FixGroups = EditorGUILayout.ToggleLeft(
                new GUIContent("Sửa group theo chuẩn (remote: Append Hash + Remote.*; local: Prevent Updates)",
                    "Không đổi asset nào, chỉ đổi schema của group."), m_FixGroups);
            m_ManualCatalogUpdate = EditorGUILayout.ToggleLeft(
                new GUIContent("Only update catalogs manually (game tự kiểm tra content mới lúc khởi động, xem README mục 7)",
                    "Bật: InitializeAsync dùng catalog đã cache, game gọi CheckForCatalogUpdates/UpdateCatalogs để hiện tiến trình và xử lý mất mạng. " +
                    "Tắt: Addressables tự đổi catalog trong InitializeAsync."), m_ManualCatalogUpdate);

            var errors = m_Config.Validate();
            foreach (var w in m_Config.Warnings()) EditorGUILayout.HelpBox(w, MessageType.Warning);
            if (errors.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", errors), MessageType.Error);

            if (errors.Count == 0)
            {
                EditorGUILayout.LabelField("Sẽ đặt", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(Preview(m_Config), EditorStyles.textArea, GUILayout.MinHeight(86));
            }

            using (new EditorGUI.DisabledScope(errors.Count > 0))
            {
                if (GUILayout.Button("Apply", GUILayout.Height(28)))
                {
                    m_Log = AddressablesSetup.Apply(m_Config, new AddressablesSetup.Options
                    {
                        ActiveProfile = AddressablesSetup.Envs[m_ActiveProfile],
                        FixGroups = m_FixGroups,
                        ManualCatalogUpdate = m_ManualCatalogUpdate,
                    });
                    Debug.Log("[Apus] Setup Addressables:\n" + string.Join("\n", m_Log));
                    ConfigChecker.RunAndLog(true);
                }
            }

            if (m_Log != null)
            {
                EditorGUILayout.HelpBox("Đã áp dụng. Kết quả kiểm tra cấu hình in ở Console.\n" + string.Join("\n", m_Log), MessageType.None);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Check Addressables Config")) ConfigChecker.RunAndLog(true);
                if (GUILayout.Button("Mở Build & Upload")) BuildAndUploadWindow.Open();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        static string Preview(PortalSetupConfig c)
        {
            var lines = new List<string>
            {
                AddressablesSetup.RemoteBuildPathVar + " = " + AddressablesSetup.RemoteBuildPathValue,
                AddressablesSetup.RemoteLoadPathVar + " = " + c.BundleLoadPath,
            };
            foreach (var env in AddressablesSetup.Envs) lines.Add(AddressablesSetup.CatalogLoadPathVar + " [" + env + "] = " + c.CatalogLoadPath(env));
            return string.Join("\n", lines);
        }
    }
}
