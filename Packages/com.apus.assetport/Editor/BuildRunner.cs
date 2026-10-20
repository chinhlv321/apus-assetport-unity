using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;

namespace Apus.AssetPort
{
    /// <summary>
    /// Runs an Addressables content build for the active build target and writes the manifest
    /// with an explicit kind. Used by the Build &amp; Upload window and the CLI.
    /// </summary>
    public static class BuildRunner
    {
        /// <param name="kind"><c>update</c> = Update a Previous Build (content state in the project); <c>baseline</c> = New Build.</param>
        public static ManifestOutcome Build(string kind)
        {
            var outcome = new ManifestOutcome();
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                outcome.Errors.Add("Project chưa có AddressableAssetSettings.");
                return outcome;
            }
            var target = EditorUserBuildSettings.activeBuildTarget.ToString();
            if (!PortalConstants.IsPlatform(target))
            {
                outcome.Errors.Add("Active build target " + target + " không được hỗ trợ (chỉ " + string.Join(", ", PortalConstants.Platforms) + ").");
                return outcome;
            }
            if (!ManifestValidator.IsValidSlug(ApusAssetPortSettings.instance.projectSlug))
            {
                outcome.Errors.Add("Chưa đặt Project slug hợp lệ (Project Settings > Apus Asset Port).");
                return outcome;
            }
            // Same checks as Apus > Check Addressables Config; errors there mean the upload would be wrong or rejected.
            var configErrors = ConfigChecker.Run().Where(c => c.Status == ConfigChecker.Status.Error).ToList();
            if (configErrors.Count > 0)
            {
                outcome.Errors.Add("Cấu hình Addressables có lỗi (" + target + ", xem Apus > Check Addressables Config), sửa rồi build lại:\n" +
                                   string.Join("\n", configErrors.Select(c => "  " + c)));
                return outcome;
            }

            AssetDatabase.SaveAssets();
            AddressablesPlayerBuildResult result;
            ManifestWriter.SuppressHook = true;
            try
            {
                if (kind == PortalConstants.KindBaseline)
                {
                    AddressableAssetSettings.BuildPlayerContent(out result);
                }
                else if (kind == PortalConstants.KindUpdate)
                {
                    var statePath = ContentUpdateScript.GetContentStateDataPath(false);
                    if (string.IsNullOrEmpty(statePath) || !File.Exists(statePath))
                    {
                        outcome.Errors.Add("Không có content state (" + statePath + "). Cần build baseline (New Build) cho " + target + " và commit addressables_content_state.bin trước.");
                        return outcome;
                    }
                    if (!CheckUpdateRestrictions(settings, statePath, outcome)) return outcome;
                    // Unlike the Groups window, BuildContentUpdate does not raise BuildScript.buildCompleted.
                    result = ContentUpdateScript.BuildContentUpdate(settings, statePath);
                    AssetDatabase.Refresh();
                }
                else
                {
                    outcome.Errors.Add("kind không hợp lệ: " + kind);
                    return outcome;
                }
            }
            finally
            {
                ManifestWriter.SuppressHook = false;
            }

            if (result == null)
            {
                outcome.Errors.Add("Build Addressables thất bại (content state không hợp lệ, xem Console).");
                return outcome;
            }
            if (!string.IsNullOrEmpty(result.Error))
            {
                outcome.Errors.Add("Build Addressables thất bại: " + result.Error);
                return outcome;
            }
            var processed = ManifestWriter.Process(result, kind);
            ConfigChecker.RunAfterBuild();
            return processed;
        }

        /// <summary>
        /// Same behaviour as the Groups window in batch mode: any modified entry in a
        /// "Prevent Updates" (Cannot Change Post Release) group fails the build (unless the check is disabled).
        /// </summary>
        static bool CheckUpdateRestrictions(AddressableAssetSettings settings, string statePath, ManifestOutcome outcome)
        {
            if (settings.CheckForContentUpdateRestrictionsOption == CheckForContentUpdateRestrictionsOptions.Disabled)
            {
                outcome.Warnings.Add("Check for Update Issues đang Disabled — không kiểm tra asset bị sửa trong group Prevent Updates (Cannot Change Post Release).");
                return true;
            }
            var modified = ContentUpdateScript.GatherModifiedEntriesWithDependencies(settings, statePath);
            if (modified == null || modified.Count == 0) return true;
            outcome.Errors.Add("Có asset trong group Prevent Updates (Cannot Change Post Release) đã bị sửa so với bản baseline; chuyển chúng sang group remote rồi build lại:\n" +
                string.Join("\n", modified.Keys.Select(e => "  " + e.AssetPath)));
            return false;
        }
    }
}
