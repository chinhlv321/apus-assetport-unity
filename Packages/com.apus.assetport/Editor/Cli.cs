using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Apus.AssetPort
{
    /// <summary>
    /// Batch-mode entry points for CI:
    /// <code>
    /// Unity -batchmode -nographics -projectPath . -buildTarget Android -logFile - \
    ///   -executeMethod Apus.AssetPort.Cli.BuildAndUpload -groupKey $COMMIT [-groupName ..] [-note ..] [-kind update|baseline] [-complete]
    /// </code>
    /// Credentials: env APUS_PORTAL_URL (optional, overrides settings), APUS_CLIENT_ID, APUS_CLIENT_SECRET.
    /// Exits with 0 on success, 1 on failure.
    /// </summary>
    public static class Cli
    {
        public static void BuildAndUpload()
        {
            var code = 1;
            try
            {
                code = RunBuildAndUpload() ? 0 : 1;
            }
            catch (Exception e)
            {
                Debug.LogError("[Apus] " + e);
            }
            EditorApplication.Exit(code);
        }

        /// <summary>Builds content per <c>-kind</c> and writes the manifest; no upload.</summary>
        public static void WriteManifestOnly()
        {
            var code = 1;
            try
            {
                var outcome = BuildRunner.Build(Kind());
                outcome.Log();
                code = outcome.Ok ? 0 : 1;
            }
            catch (Exception e)
            {
                Debug.LogError("[Apus] " + e);
            }
            EditorApplication.Exit(code);
        }

        static bool RunBuildAndUpload()
        {
            var groupKey = Arg("-groupKey");
            if (string.IsNullOrEmpty(groupKey))
            {
                Debug.LogError("[Apus] Thiếu -groupKey <key> (ví dụ commit hash).");
                return false;
            }
            var kind = Kind();
            var groupName = Arg("-groupName") ?? groupKey;
            var note = Arg("-note") ?? "";
            var complete = HasFlag("-complete");

            var problem = ApusCredentials.Problem();
            if (problem != null)
            {
                Debug.LogError("[Apus] " + problem);
                return false;
            }

            var outcome = BuildRunner.Build(kind);
            outcome.Log();
            if (!outcome.Ok) return false;

            var project = ApusAssetPortSettings.instance.projectSlug;
            var portal = ApusCredentials.PortalUrl;
            using (var client = new PortalClient(portal, ApusCredentials.ClientId, ApusCredentials.ClientSecret, ApusCredentials.DevEmail))
            {
                var progress = new UploadProgress();
                var task = Task.Run(async () =>
                {
                    var groupId = await Uploader.EnsureGroup(client, project, groupName, note, groupKey, CancellationToken.None).ConfigureAwait(false);
                    Debug.Log("[Apus] Group #" + groupId + " (" + Uploader.GroupUrl(portal, groupId) + ")");
                    var result = await Uploader.UploadRelease(client, groupId, outcome.OutputDir, progress, m => Debug.Log("[Apus] " + m), CancellationToken.None).ConfigureAwait(false);
                    foreach (var w in result.Warnings) Debug.LogWarning("[Apus] " + w);
                    Debug.Log("[Apus] " + result.Platform + ": upload " + result.Uploaded + " bundle, bỏ qua " + result.Skipped + " bundle đã có, release #" + result.ReleaseId);
                    if (complete)
                    {
                        var status = await Uploader.CompleteGroup(client, groupId, CancellationToken.None).ConfigureAwait(false);
                        Debug.Log("[Apus] Đã chốt group #" + groupId + " → " + status + ". Publish trên portal: " + Uploader.GroupUrl(portal, groupId));
                    }
                    else
                    {
                        Debug.Log("[Apus] Group #" + groupId + " vẫn là draft (job cuối truyền -complete để chốt).");
                    }
                });
                // Upload code never touches the main thread, so blocking here cannot deadlock.
                task.GetAwaiter().GetResult();
            }
            return true;
        }

        static string Kind()
        {
            var kind = (Arg("-kind") ?? PortalConstants.KindUpdate).ToLowerInvariant();
            if (kind != PortalConstants.KindUpdate && kind != PortalConstants.KindBaseline)
                throw new ArgumentException("-kind phải là update hoặc baseline, nhận được '" + kind + "'");
            return kind;
        }

        /// <summary>Value after <paramref name="name"/>; null when the flag is absent. A flag followed by another flag (or nothing) is an error.</summary>
        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 >= args.Length || LooksLikeFlag(args[i + 1]))
                    throw new ArgumentException("Thiếu giá trị sau " + name + ".");
                return args[i + 1];
            }
            return null;
        }

        static bool LooksLikeFlag(string arg) => arg.Length > 1 && arg[0] == '-' && char.IsLetter(arg[1]);

        static bool HasFlag(string name) =>
            Environment.GetCommandLineArgs().Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    }
}
