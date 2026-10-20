using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Apus.AssetPort
{
    /// <summary>Checks the Addressables configuration against the required setup (README "Cấu hình Addressables").</summary>
    public static class ConfigChecker
    {
        public enum Status { Pass, Warn, Error }

        public struct Check
        {
            public Status Status;
            public string Title;
            public string Hint;

            public override string ToString() =>
                (Status == Status.Pass ? "[OK]   " : Status == Status.Warn ? "[WARN] " : "[LỖI]  ") + Title + (Status != Status.Pass && !string.IsNullOrEmpty(Hint) ? "\n         → " + Hint : "");
        }

        [MenuItem("Apus/Check Addressables Config", priority = 20)]
        static void Menu() => RunAndLog(true);

        public static List<Check> Run()
        {
            var checks = new List<Check>();
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var apus = ApusAssetPortSettings.instance;

            Add(checks, ManifestValidator.IsValidSlug(apus.projectSlug), Status.Error,
                "Project slug = '" + apus.projectSlug + "'",
                "Đặt slug của project ở Project Settings > Apus Asset Port.");

            if (settings == null)
            {
                Add(checks, false, Status.Error, "Có AddressableAssetSettings", "Mở Window > Asset Management > Addressables > Groups và tạo settings.");
                return checks;
            }

            var target = EditorUserBuildSettings.activeBuildTarget.ToString();
            var appVersion = PlayerSettings.bundleVersion;

            Add(checks, PortalConstants.IsPlatform(target), Status.Warn,
                "Active build target = " + target, "Portal chỉ nhận " + string.Join(", ", PortalConstants.Platforms) + ". Switch Platform trước khi build.");
            Add(checks, ManifestValidator.IsValidAppVersion(appVersion), Status.Error,
                "App version (PlayerSettings.bundleVersion) = '" + appVersion + "'", "Chỉ gồm [0-9A-Za-z._-], tối đa 32 ký tự.");

            Add(checks, settings.BuildRemoteCatalog, Status.Error,
                "Build Remote Catalog đang bật", "Addressable Asset Settings > Catalog > Build Remote Catalog.");

            Add(checks, settings.OverridePlayerVersion == "content", Status.Error,
                "Player Version Override = 'content' (đang là '" + settings.OverridePlayerVersion + "')",
                "Addressable Asset Settings > Catalog > Player Version Override = content, để tên catalog cố định catalog_content.*");

#if APUS_ADDRESSABLES_2
            Add(checks, true, Status.Pass, "Định dạng catalog: " + (settings.EnableJsonCatalog ? "JSON (.json)" : "binary (.bin)"), null);
#endif

            string catalogBuild = null;
            if (settings.BuildRemoteCatalog)
            {
                catalogBuild = Eval(settings, settings.RemoteCatalogBuildPath);
                var catalogLoad = Eval(settings, settings.RemoteCatalogLoadPath);
                Add(checks, catalogBuild != null && ContainsSegment(catalogBuild, target), Status.Warn,
                    "Remote catalog build path chứa [BuildTarget]: " + catalogBuild,
                    "Đặt Build Path của remote catalog = ServerData/[BuildTarget] (giống Remote.BuildPath).");
                Add(checks, catalogLoad != null && ContainsSegment(catalogLoad, target) && ContainsSegment(catalogLoad, appVersion), Status.Warn,
                    "Remote catalog load path chứa [BuildTarget] và appVersion: " + catalogLoad,
                    "Load Path của remote catalog = https://cdn.<domain>/live/<project>/<envPath>/[BuildTarget]/[UnityEngine.Application.version] (một profile cho mỗi env).");
                if (catalogLoad != null && !catalogLoad.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    Add(checks, false, Status.Warn, "Remote catalog load path dùng https", "iOS chặn http (ATS). Dùng https://cdn.<domain>/...");
            }

            var buildWithPlayer = settings.BuildAddressablesWithPlayerBuild;
            if (buildWithPlayer == AddressableAssetSettings.PlayerBuildOption.PreferencesValue)
                Add(checks, false, Status.Warn, "Build Addressables on Player Build = theo Preferences",
                    "Đặt Addressable Asset Settings > Build > Build Addressables on Player Build = 'Build Addressables content on Player Build' để mọi máy build giống nhau.");
            else
                Add(checks, buildWithPlayer == AddressableAssetSettings.PlayerBuildOption.BuildWithPlayer, Status.Warn,
                    "Build Addressables on Player Build đang bật", "Addressable Asset Settings > Build > Build Addressables on Player Build.");

            Add(checks, settings.CheckForContentUpdateRestrictionsOption == CheckForContentUpdateRestrictionsOptions.FailBuild, Status.Warn,
                "Update a Previous Build > Check for Update Issues = Fail Build (đang là " + settings.CheckForContentUpdateRestrictionsOption + ")",
                "Addressable Asset Settings > Update a Previous Build > Check For Update Issues = Fail Build.");

            CheckContentStatePath(settings, checks);
            CheckGroups(settings, catalogBuild, checks);
            return checks;
        }

        static void CheckContentStatePath(AddressableAssetSettings settings, List<Check> checks)
        {
            var raw = settings.ContentStateBuildPath;
            // Empty = default Assets/AddressableAssetsData/<platform>.
            var evaluated = string.IsNullOrEmpty(raw) ? "Assets/AddressableAssetsData/" + EditorUserBuildSettings.activeBuildTarget : settings.profileSettings.EvaluateString(settings.activeProfileId, raw);
            var underAssets = false;
            try
            {
                var full = Path.GetFullPath(evaluated);
                var assets = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
                underAssets = (full + Path.DirectorySeparatorChar).StartsWith(assets, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // invalid or remote path: underAssets stays false
            }
            Add(checks, underAssets, Status.Warn, "Content State Build Path nằm trong Assets/: " + evaluated,
                "Để trống Content State Build Path (mặc định Assets/AddressableAssetsData/<platform>) và commit addressables_content_state.bin.");
        }

        static void CheckGroups(AddressableAssetSettings settings, string catalogBuild, List<Check> checks)
        {
            var remoteBad = new List<string>();
            var remoteElsewhere = new List<string>();
            var localNotStatic = new List<string>();
            var remoteCount = 0;
            foreach (var group in settings.groups)
            {
                if (group == null) continue;
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema == null || !schema.IncludeInBuild) continue;
                var load = Eval(settings, schema.LoadPath) ?? "";
                var isRemote = load.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || load.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (isRemote)
                {
                    remoteCount++;
                    if (schema.BundleNaming != BundledAssetGroupSchema.BundleNamingStyle.AppendHash) remoteBad.Add(group.Name + " (" + schema.BundleNaming + ")");
                    var build = Eval(settings, schema.BuildPath);
                    if (catalogBuild != null && build != null && !SamePath(build, catalogBuild)) remoteElsewhere.Add(group.Name + " → " + build);
                }
                else
                {
                    var cu = group.GetSchema<ContentUpdateGroupSchema>();
                    if (cu == null || !cu.StaticContent) localNotStatic.Add(group.Name);
                }
            }

            Add(checks, remoteCount > 0, Status.Warn, "Có " + remoteCount + " group remote", "Tạo ít nhất 1 group dùng Remote.BuildPath/Remote.LoadPath.");
            Add(checks, remoteBad.Count == 0, Status.Warn, "Group remote dùng Bundle Naming = Append Hash" + List(remoteBad),
                "Group > Advanced Options > Bundle Naming Mode = Append Hash to Filename. Không có hash thì bundle trùng tên nhưng khác nội dung sẽ bị portal chặn.");
            Add(checks, remoteElsewhere.Count == 0, Status.Error, "Group remote build vào cùng thư mục với remote catalog" + List(remoteElsewhere),
                "Remote.BuildPath của group và Build Path của remote catalog phải giống nhau (ServerData/[BuildTarget]); bundle ở chỗ khác sẽ không vào manifest.");
            Add(checks, localNotStatic.Count == 0, Status.Warn, "Group local bật Prevent Updates (Cannot Change Post Release)" + List(localNotStatic),
                "Group local > Content Update Restriction > bật Prevent Updates (xem README).");
        }

        public static void RunAndLog(bool includePassed)
        {
            var checks = Run();
            var sb = new StringBuilder("[Apus] Kiểm tra cấu hình Addressables:\n");
            foreach (var c in checks.Where(c => includePassed || c.Status != Status.Pass)) sb.AppendLine(c.ToString());
            if (checks.Any(c => c.Status == Status.Error)) Debug.LogError(sb.ToString());
            else if (checks.Any(c => c.Status == Status.Warn)) Debug.LogWarning(sb.ToString());
            else Debug.Log(sb.ToString());
        }

        /// <summary>Called after each build: logs only when something is off.</summary>
        public static void RunAfterBuild()
        {
            if (Run().Any(c => c.Status != Status.Pass)) RunAndLog(false);
        }

        static void Add(List<Check> checks, bool ok, Status failStatus, string title, string hint) =>
            checks.Add(new Check { Status = ok ? Status.Pass : failStatus, Title = title, Hint = hint });

        static string List(List<string> items) => items.Count == 0 ? "" : ": " + string.Join(", ", items);

        static string Eval(AddressableAssetSettings settings, ProfileValueReference reference)
        {
            try
            {
                var v = reference?.GetValue(settings);
                return string.IsNullOrEmpty(v) || v == AddressableAssetProfileSettings.undefinedEntryValue ? null : v;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static bool ContainsSegment(string path, string segment) =>
            !string.IsNullOrEmpty(segment) && path.Replace('\\', '/').Split('/').Contains(segment, StringComparer.Ordinal);

        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
