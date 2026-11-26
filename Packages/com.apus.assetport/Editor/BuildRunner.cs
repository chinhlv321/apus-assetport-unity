using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Apus.AssetPort
{
    /// <summary>
    /// Extension points for project code (Editor assembly referencing Apus.AssetPort.Editor), all raised on the main thread
    /// by the Build &amp; Upload window and the CLI. Subscribe from an <c>[InitializeOnLoad]</c> static constructor.
    /// </summary>
    public static class ApusHooks
    {
        /// <summary>Before the Addressables content build; arg = kind (update|baseline). Throw to cancel the build (message shown as the error).</summary>
        public static event Action<string> BeforeBuild;
        /// <summary>After a content build, successful or not (check <see cref="ManifestOutcome.Ok"/>).</summary>
        public static event Action<ManifestOutcome> AfterBuild;
        /// <summary>After one platform's release has been uploaded to the portal.</summary>
        public static event Action<ReleaseUploadResult> AfterUpload;
        /// <summary>After the group was completed (all platforms uploaded): group id and its portal URL. Not published yet.</summary>
        public static event Action<long, string> AfterGroupCompleted;

        internal static void RaiseBeforeBuild(string kind) => BeforeBuild?.Invoke(kind);
        internal static void RaiseAfterBuild(ManifestOutcome outcome) => Safe(() => AfterBuild?.Invoke(outcome), "AfterBuild");
        internal static void RaiseAfterUpload(ReleaseUploadResult result) => Safe(() => AfterUpload?.Invoke(result), "AfterUpload");
        internal static void RaiseAfterGroupCompleted(long groupId, string url) => Safe(() => AfterGroupCompleted?.Invoke(groupId, url), "AfterGroupCompleted");

        // A broken handler must not turn a finished build/upload into a failure.
        static void Safe(Action a, string name)
        {
            try { a(); }
            catch (Exception e) { UnityEngine.Debug.LogError("[Apus] ApusHooks." + name + " handler lỗi: " + e); }
        }
    }

    /// <summary>
    /// With "Build Addressables on Player Build" off, the player ships the content built earlier, whose settings.json has
    /// the catalog URL (env path + app version) baked in. Fails the player build when that URL is not the one the active
    /// profile and app version need, instead of shipping an app that reads another env's or version's catalog.
    /// </summary>
    public class PlayerBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null || !settings.BuildRemoteCatalog) return;
            if (settings.BuildAddressablesWithPlayerBuild != AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer) return;
            // Only projects configured by Apus > Setup Addressables…
            if (settings.RemoteCatalogLoadPath.GetName(settings) != AddressablesSetup.CatalogLoadPathVar) return;
            var path = UnityEngine.AddressableAssets.Addressables.BuildPath + "/settings.json";
            var problem = Problem(File.Exists(path) ? File.ReadAllText(path) : null, settings.RemoteCatalogLoadPath.GetValue(settings));
            if (problem != null) throw new BuildFailedException("[Apus] " + problem);
        }

        /// <summary>Null when the built content's catalog URL matches <paramref name="expectedLoadPath"/>, else what is wrong.</summary>
        public static string Problem(string settingsJson, string expectedLoadPath)
        {
            const string fix = " Build Addressables on Player Build đang tắt: build lại content (Apus > Build & Upload) rồi build player.";
            if (settingsJson == null) return "Chưa có content Addressables đã build cho platform này." + fix;
            if (string.IsNullOrEmpty(expectedLoadPath)) return null;
            var json = settingsJson.Replace("\\/", "/");
            var expected = expectedLoadPath.TrimEnd('/');
            if (json.Contains("\"" + expected + "/catalog_")) return null;
            var found = Regex.Match(json, "\"(https?://[^\"]+)/catalog_[^\"/]*\\.hash\"");
            return "Content đã build " + (found.Success ? "trỏ catalog tới '" + found.Groups[1].Value + "'" : "không có remote catalog") +
                   ", nhưng profile và app version hiện tại cần '" + expected + "'." + fix;
        }
    }

    /// <summary>
    /// Runs an Addressables content build for the active build target and writes the manifest
    /// with an explicit kind. Used by the Build &amp; Upload window and the CLI.
    /// </summary>
    public static class BuildRunner
    {
        /// <param name="kind"><c>update</c> = Update a Previous Build (content state in the project); <c>baseline</c> = New Build.</param>
        public static ManifestOutcome Build(string kind)
        {
            var outcome = BuildCore(kind);
            ApusHooks.RaiseAfterBuild(outcome);
            return outcome;
        }

        /// <summary>Makes the Addressables profile named <paramref name="name"/> active. Returns false when it doesn't exist.</summary>
        public static bool SetActiveProfile(string name)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var id = settings == null ? null : settings.profileSettings.GetProfileId(name);
            if (string.IsNullOrEmpty(id)) return false;
            settings.activeProfileId = id;
            return true;
        }

        static ManifestOutcome BuildCore(string kind)
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

            try
            {
                ApusHooks.RaiseBeforeBuild(kind);
            }
            catch (Exception e)
            {
                outcome.Errors.Add("ApusHooks.BeforeBuild hủy build: " + e.Message);
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
