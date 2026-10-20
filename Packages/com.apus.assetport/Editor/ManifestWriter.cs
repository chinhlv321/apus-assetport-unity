using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Apus.AssetPort
{
    /// <summary>Result of turning an Addressables build result into <c>apus-manifest.json</c>.</summary>
    public class ManifestOutcome
    {
        public bool Written;
        public string OutputDir;
        public string ManifestPath;
        public ApusManifest Manifest;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Deleted = new List<string>();

        public bool Ok => Written && Errors.Count == 0;

        public void Log()
        {
            foreach (var w in Warnings) Debug.LogWarning("[Apus] " + w);
            foreach (var e in Errors) Debug.LogError("[Apus] " + e);
            if (Deleted.Count > 0)
                Debug.Log("[Apus] Đã xoá " + Deleted.Count + " file cũ không thuộc build này trong " + OutputDir + ":\n" + string.Join("\n", Deleted));
            if (Written)
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[Apus] Đã ghi {0} ({1} {2}, appVersion {3}, kind {4}, {5} file, {6:0.0} MB)",
                    ManifestPath, Manifest.project, Manifest.platform, Manifest.appVersion, Manifest.kind,
                    Manifest.files.Count, Manifest.TotalBytes / 1048576.0));
        }
    }

    /// <summary>
    /// Writes <c>apus-manifest.json</c> into the remote output folder after every successful
    /// Addressables content build (including builds started from the Addressables Groups window),
    /// and deletes stale files left in that folder by older builds.
    /// </summary>
    [InitializeOnLoad]
    public static class ManifestWriter
    {
        /// <summary>Set by <see cref="BuildRunner"/>, which processes its own results with an explicit kind.</summary>
        internal static bool SuppressHook;

        static ManifestWriter()
        {
            BuildScript.buildCompleted -= OnBuildCompleted;
            BuildScript.buildCompleted += OnBuildCompleted;
        }

        static void OnBuildCompleted(AddressableAssetBuildResult result)
        {
            if (SuppressHook) return;
            if (!(result is AddressablesPlayerBuildResult playerResult) || !string.IsNullOrEmpty(result.Error)) return;
            if (!ApusAssetPortSettings.instance.generateManifest) return;
            try
            {
                Process(playerResult, null).Log();
                ConfigChecker.RunAfterBuild();
            }
            catch (Exception e)
            {
                Debug.LogError("[Apus] Không ghi được apus-manifest.json: " + e);
            }
        }

        /// <param name="result">A successful player-content build result.</param>
        /// <param name="explicitKind"><c>baseline</c>/<c>update</c>, or null to infer from the result.</param>
        public static ManifestOutcome Process(AddressablesPlayerBuildResult result, string explicitKind)
        {
            var outcome = new ManifestOutcome();
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                outcome.Errors.Add("Project chưa có AddressableAssetSettings.");
                return outcome;
            }
            if (!settings.BuildRemoteCatalog)
            {
                const string off = "Build Remote Catalog đang tắt nên không có remote catalog để upload. Bật Addressable Asset Settings > Catalog > Build Remote Catalog rồi build lại.";
                // Build & Upload / CLI asked for a manifest explicitly: that is a failure, not a skipped step.
                if (explicitKind != null) outcome.Errors.Add(off);
                else outcome.Warnings.Add(off + " (bỏ qua apus-manifest.json)");
                return outcome;
            }

            var dir = RemoteOutputDir(result, settings);
            if (dir == null)
            {
                outcome.Errors.Add("Không xác định được thư mục build của remote catalog (Remote Catalog Build Path).");
                return outcome;
            }
            outcome.OutputDir = dir;
            outcome.ManifestPath = Path.Combine(dir, PortalConstants.ManifestFileName);
            // Never leave a manifest from an older build next to new files.
            var hadManifest = File.Exists(outcome.ManifestPath);
            if (hadManifest) File.Delete(outcome.ManifestPath);

            var platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            if (!PortalConstants.IsPlatform(platform))
            {
                outcome.Warnings.Add("Platform " + platform + " không được portal hỗ trợ (chỉ " + string.Join(", ", PortalConstants.Platforms) + ") — không ghi apus-manifest.json.");
                return outcome;
            }

            var outputs = CollectOutputFiles(result, settings, dir, outcome);
            if (outcome.Errors.Count > 0) return outcome;

            var manifest = new ApusManifest
            {
                project = ApusAssetPortSettings.instance.projectSlug,
                platform = platform,
                appVersion = PlayerSettings.bundleVersion,
                kind = explicitKind ?? InferKind(result, outcome),
                unityVersion = Application.unityVersion,
                addressablesVersion = AddressablesVersion(),
                gitCommit = GitCommit(),
                builtAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            };
            foreach (var path in outputs.OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                var info = new FileInfo(path);
                if (info.Length > PortalConstants.LargeBundleWarnBytes)
                    outcome.Warnings.Add(string.Format(CultureInfo.InvariantCulture, "{0} lớn {1:0} MB — người chơi sẽ tải lâu.", info.Name, info.Length / 1048576.0));
                manifest.files.Add(new ManifestFile { name = info.Name, size = info.Length, md5 = Md5Hex(path) });
            }
            outcome.Manifest = manifest;
            if (!manifest.files.Any(f => !PortalConstants.IsCatalogFile(f.name)))
                outcome.Warnings.Add("Build không có bundle remote nào trong " + dir + " — kiểm tra Build Path của các group remote.");

            outcome.Errors.AddRange(ManifestValidator.Validate(manifest).Select(e => "Manifest không hợp lệ: " + e));
            // Nothing is deleted unless the build produced a valid manifest.
            if (outcome.Errors.Count > 0) return outcome;

            CleanStaleFiles(dir, platform, hadManifest, outputs, outcome);

            File.WriteAllText(outcome.ManifestPath, manifest.ToJson(), new UTF8Encoding(false));
            outcome.Written = true;
            return outcome;
        }

        /// <summary>Absolute folder that holds the remote catalog (and remote bundles) of this build.</summary>
        static string RemoteOutputDir(AddressablesPlayerBuildResult result, AddressableAssetSettings settings)
        {
            // Both Addressables 1.21 and 2.x declare these properties but never assign them; keep them as the
            // first choice in case a future version does.
            var fromResult = !string.IsNullOrEmpty(result.RemoteCatalogHashFilePath) ? result.RemoteCatalogHashFilePath : result.RemoteCatalogJsonFilePath;
            if (!string.IsNullOrEmpty(fromResult)) return Path.GetDirectoryName(Path.GetFullPath(fromResult));

            var buildPath = settings.RemoteCatalogBuildPath.GetValue(settings);
            if (string.IsNullOrEmpty(buildPath) || buildPath == AddressableAssetProfileSettings.undefinedEntryValue) return null;
            return Path.GetFullPath(buildPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        static HashSet<string> CollectOutputFiles(AddressablesPlayerBuildResult result, AddressableAssetSettings settings, string dir, ManifestOutcome outcome)
        {
            var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Remote catalog: from the build's file registry, falling back to the well-known names.
            var registered = result.FileRegistry?.GetFilePaths() ?? Enumerable.Empty<string>();
            foreach (var path in registered)
            {
                var full = Path.GetFullPath(path);
                if (SameDir(Path.GetDirectoryName(full), dir) && IsRemoteCatalogName(Path.GetFileName(full)) && File.Exists(full))
                    outputs.Add(full);
            }
            if (outputs.Count == 0)
            {
                var stem = "catalog_" + settings.PlayerBuildVersion;
                foreach (var ext in new[] { ".hash", ".json", ".bin" })
                {
                    var p = Path.Combine(dir, stem + ext);
                    if (File.Exists(p)) outputs.Add(p);
                }
            }
            KeepOneCatalogData(outputs, settings, outcome);
            if (!outputs.Any(p => p.EndsWith(".hash", StringComparison.OrdinalIgnoreCase)))
            {
                outcome.Errors.Add("Không tìm thấy remote catalog (.hash) trong " + dir + ". Kiểm tra Build Remote Catalog và Remote Catalog Build Path.");
                return outputs;
            }

            foreach (var bundle in result.AssetBundleBuildResults)
            {
                if (string.IsNullOrEmpty(bundle.FilePath)) continue;
                var full = Path.GetFullPath(bundle.FilePath);
                var parent = Path.GetDirectoryName(full);
                if (SameDir(parent, dir))
                {
                    if (File.Exists(full)) outputs.Add(full);
                    else outcome.Errors.Add("Bundle trong kết quả build không tồn tại trên đĩa: " + full + " (Bundle Naming phải là Append Hash).");
                }
                else if (IsInside(full, dir))
                {
                    outcome.Errors.Add("Bundle nằm trong thư mục con, portal không hỗ trợ: " + full);
                }
                else if (bundle.SourceAssetGroup != null && IsRemoteGroup(settings, bundle.SourceAssetGroup))
                {
                    // A remote bundle outside the catalog folder would be missing from the manifest and never uploaded,
                    // while the catalog still points players at it.
                    outcome.Errors.Add("Bundle của group remote '" + bundle.SourceAssetGroup.Name + "' nằm ngoài thư mục remote catalog " + dir + ": " + full +
                                       ". Đặt Build Path của group = Build Path của remote catalog (ServerData/[BuildTarget]).");
                }
            }
            return outputs;
        }

        static bool IsRemoteGroup(AddressableAssetSettings settings, AddressableAssetGroup group)
        {
            try
            {
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                var load = schema?.LoadPath?.GetValue(settings) ?? "";
                return load.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || load.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// A folder can hold both catalog_content.json and catalog_content.bin when the catalog format was switched
        /// (Addressables 2.x "Enable Json Catalog"); the older one is left over. Keep the one in the current format,
        /// else the newest.
        /// </summary>
        static void KeepOneCatalogData(HashSet<string> outputs, AddressableAssetSettings settings, ManifestOutcome outcome)
        {
            var data = outputs.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToList();
            if (data.Count <= 1) return;
#if APUS_ADDRESSABLES_2
            var preferred = settings.EnableJsonCatalog ? ".json" : ".bin";
#else
            var preferred = ".json";
#endif
            var keep = data.FirstOrDefault(p => p.EndsWith(preferred, StringComparison.OrdinalIgnoreCase)) ??
                       data.OrderByDescending(p => File.GetLastWriteTimeUtc(p)).First();
            foreach (var stale in data.Where(d => !string.Equals(d, keep, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                outputs.Remove(stale);
                outcome.Warnings.Add("Bỏ qua catalog cũ " + Path.GetFileName(stale) + " (dùng " + Path.GetFileName(keep) + ").");
            }
        }

        static bool IsRemoteCatalogName(string name) =>
            name.StartsWith("catalog_", StringComparison.Ordinal) &&
            (name.EndsWith(".hash", StringComparison.Ordinal) || name.EndsWith(".json", StringComparison.Ordinal) || name.EndsWith(".bin", StringComparison.Ordinal));

        /// <summary>
        /// Addressables never removes bundles of older builds from ServerData. There is no pre-build hook for
        /// builds started from the Groups window, so stale files are removed after the build instead.
        /// </summary>
        static void CleanStaleFiles(string dir, string platform, bool hadManifest, HashSet<string> outputs, ManifestOutcome outcome)
        {
            var unsafeReason = UnsafeToClean(dir);
            // Only a folder that is clearly this platform's remote output may be emptied: named after the build
            // target (ServerData/[BuildTarget]) or already used by this package (had an apus-manifest.json).
            if (unsafeReason == null && !hadManifest && !string.Equals(Path.GetFileName(dir), platform, StringComparison.Ordinal))
                unsafeReason = "tên thư mục không phải " + platform + " và chưa từng có " + PortalConstants.ManifestFileName +
                               " (đặt Remote Catalog Build Path = ServerData/[BuildTarget] để package tự dọn bundle cũ)";
            if (unsafeReason != null)
            {
                outcome.Warnings.Add("Không dọn file cũ trong " + dir + ": " + unsafeReason);
                return;
            }
            foreach (var file in Directory.GetFiles(dir))
            {
                var full = Path.GetFullPath(file);
                if (outputs.Contains(full)) continue;
                if (string.Equals(Path.GetFileName(full), PortalConstants.ManifestFileName, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    File.Delete(full);
                    outcome.Deleted.Add(Path.GetFileName(full));
                }
                catch (Exception e)
                {
                    outcome.Warnings.Add("Không xoá được " + full + ": " + e.Message);
                }
            }
            if (Directory.GetDirectories(dir).Length > 0)
                outcome.Warnings.Add("Thư mục " + dir + " có thư mục con; portal chỉ nhận file ở cấp đầu, các thư mục con bị bỏ qua.");
        }

        static string UnsafeToClean(string dir)
        {
            var projectRoot = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
            if (string.Equals(Path.GetPathRoot(dir), dir, StringComparison.OrdinalIgnoreCase) || dir.Length <= 3) return "là thư mục gốc của ổ đĩa";
            if (SameDir(dir, projectRoot)) return "là thư mục gốc của project";
            if (IsInside(projectRoot, dir)) return "chứa cả project";
            foreach (var protectedDir in new[] { "Assets", "Library", "Packages", "ProjectSettings", "UserSettings", "Temp", "Logs" })
            {
                var p = Path.Combine(projectRoot, protectedDir);
                if (SameDir(dir, p) || IsInside(dir, p)) return "nằm trong " + protectedDir + "/";
            }
            return null;
        }

        static string InferKind(AddressablesPlayerBuildResult result, ManifestOutcome outcome)
        {
            // IsUpdateContentBuild is set by both 1.21 and 2.x (PreviousContentState != null).
            var byFlag = result.IsUpdateContentBuild ? PortalConstants.KindUpdate : PortalConstants.KindBaseline;

            // Heuristic: a baseline build writes the content state file.
            var statePath = !string.IsNullOrEmpty(result.ContentStateFilePath) ? result.ContentStateFilePath : ContentUpdateScript.GetContentStateDataPath(false);
            var buildStart = DateTime.UtcNow - TimeSpan.FromSeconds(result.Duration) - TimeSpan.FromSeconds(5);
            var stateWritten = !string.IsNullOrEmpty(statePath) && File.Exists(statePath) && File.GetLastWriteTimeUtc(statePath) >= buildStart;
            var byState = stateWritten ? PortalConstants.KindBaseline : PortalConstants.KindUpdate;

            if (byFlag != byState)
                outcome.Warnings.Add("Không chắc kind của build (IsUpdateContentBuild → " + byFlag + ", content state → " + byState + "); dùng " + byFlag + ".");
            return byFlag;
        }

        public static string AddressablesVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AddressableAssetSettings).Assembly);
            return info != null ? info.version : "";
        }

        public static string GitCommit()
        {
            try
            {
                var root = Path.GetDirectoryName(Application.dataPath);
                var head = RunGit(root, "rev-parse HEAD");
                if (head == null) return null;
                head = head.Trim();
                if (head.Length < 40 || !head.All(Uri.IsHexDigit)) return null;
                head = head.ToLowerInvariant();
                // Uncommitted changes to tracked files: the build does not match the commit exactly.
                var status = RunGit(root, "status --porcelain --untracked-files=no");
                return !string.IsNullOrWhiteSpace(status) ? head + "-dirty" : head;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Stdout of a git command, or null on failure. Stdout and stderr are drained concurrently (no pipe deadlock).</summary>
        static string RunGit(string workingDirectory, string arguments)
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                if (p == null) return null;
                var stderr = p.StandardError.ReadToEndAsync();
                var stdout = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(10000))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (Exception)
                    {
                        // already exited
                    }
                    return null;
                }
                if (!stdout.Wait(5000)) return null;
                stderr.Wait(1000);
                return p.ExitCode == 0 ? stdout.Result : null;
            }
        }

        public static string Md5Hex(string path)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(path))
            {
                var hash = md5.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        static bool SameDir(string a, string b) =>
            string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="dir"/>.</summary>
        static bool IsInside(string path, string dir)
        {
            var d = Trim(dir) + Path.DirectorySeparatorChar;
            return Trim(path).StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        static string Trim(string p) =>
            Path.GetFullPath(p).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
    }
}
