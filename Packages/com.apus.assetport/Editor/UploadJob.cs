using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Apus.AssetPort
{
    /// <summary>
    /// Multi-platform "switch platform → build → upload" job behind the Build &amp; Upload window.
    /// The state lives in <see cref="SessionState"/> so the job resumes after the domain reload that
    /// follows a platform switch.
    /// </summary>
    [InitializeOnLoad]
    public static class UploadJob
    {
        const string k_Key = "Apus.AssetPort.UploadJob";
        /// <summary>The log lives in SessionState and is drawn every repaint: keep only the newest lines.</summary>
        const int k_MaxLogLines = 2000;

        public enum Phase { Switch, Build, Upload, Uploading, Complete, Completing, Done, Failed }

        [Serializable]
        public class State
        {
            public List<string> platforms = new List<string>();
            public int index;
            public Phase phase;
            public string kind;
            public string groupName;
            public string note;
            public string groupKey;
            public long groupId;
            public string portalUrl;
            public string outputDir;
            public long switchRequestedTicks;
            public string error;
            public List<string> log = new List<string>();
            public List<string> summary = new List<string>();

            public string CurrentPlatform => index < platforms.Count ? platforms[index] : null;
            public bool Running => phase != Phase.Done && phase != Phase.Failed;
        }

        static State s_State;
        static Task s_Task;
        static CancellationTokenSource s_Cts;
        static PortalClient s_Client;
        static long s_PendingGroupId;
        static ReleaseUploadResult s_LastRelease;
        static string s_LastCompleteStatus;
        static readonly List<string> s_PendingLog = new List<string>();

        public static readonly UploadProgress Progress = new UploadProgress();

        public static event Action Changed;

        static UploadJob()
        {
            s_State = Load();
            if (s_State != null && s_State.Running) EditorApplication.update += Tick;
        }

        public static State Current => s_State;

        public static void Start(List<string> platforms, string kind, string groupName, string note)
        {
            if (s_State != null && s_State.Running) throw new InvalidOperationException("Đang có job chạy.");
            s_State = new State
            {
                platforms = platforms,
                kind = kind,
                groupName = groupName,
                note = note,
                groupKey = "editor-" + Guid.NewGuid().ToString("N"),
                portalUrl = ApusCredentials.PortalUrl,
                phase = Phase.Switch,
            };
            AddLog("Bắt đầu: " + string.Join(", ", platforms) + ", " + kind + ", group '" + groupName + "'");
            Save();
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        public static void Cancel()
        {
            if (s_State == null || !s_State.Running) return;
            s_Cts?.Cancel();
            Fail("Đã huỷ. Group nháp (nếu đã tạo) sẽ bị portal tự xoá sau 24 giờ.");
        }

        public static void Clear()
        {
            if (s_State != null && s_State.Running) return;
            s_State = null;
            SessionState.EraseString(k_Key);
            Changed?.Invoke();
        }

        static void Tick()
        {
            if (s_State == null || !s_State.Running)
            {
                EditorApplication.update -= Tick;
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            FlushPendingLog();

            try
            {
                switch (s_State.phase)
                {
                    case Phase.Switch: DoSwitch(); break;
                    case Phase.Build: DoBuild(); break;
                    case Phase.Upload: StartUpload(); break;
                    case Phase.Uploading: PollUpload(); break;
                    case Phase.Complete: StartComplete(); break;
                    case Phase.Completing: PollComplete(); break;
                }
            }
            catch (Exception e)
            {
                Fail(e.Message);
                Debug.LogException(e);
            }
        }

        static void DoSwitch()
        {
            var target = ParseTarget(s_State.CurrentPlatform);
            s_State.phase = Phase.Build;
            Save();
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                AddLog("Switch platform → " + target);
                s_State.switchRequestedTicks = DateTime.UtcNow.Ticks;
                Save();
                // Triggers a domain reload; the job continues from SessionState afterwards.
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(target), target))
                    Fail("Không switch được sang " + target + " (đã cài module Build Support chưa?).");
            }
        }

        static void DoBuild()
        {
            var target = ParseTarget(s_State.CurrentPlatform);
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                // The switch may complete on a later editor update; give it time before giving up.
                if (DateTime.UtcNow.Ticks - s_State.switchRequestedTicks < TimeSpan.FromMinutes(30).Ticks) return;
                Fail("Platform hiện tại là " + EditorUserBuildSettings.activeBuildTarget + ", cần " + target + ".");
                return;
            }
            AddLog(target + ": build Addressables (" + s_State.kind + ")…");
            Save();
            var outcome = BuildRunner.Build(s_State.kind);
            outcome.Log();
            foreach (var w in outcome.Warnings) AddLog("⚠ " + w);
            if (!outcome.Ok)
            {
                var reasons = outcome.Errors.Count > 0 ? outcome.Errors : outcome.Warnings;
                Fail(target + ": build thất bại" + (reasons.Count > 0 ? ".\n" + string.Join("\n", reasons) : " (không ghi được apus-manifest.json, xem Console)."));
                return;
            }
            AddLog(target + ": " + outcome.Manifest.files.Count + " file, manifest " + outcome.ManifestPath);
            s_State.outputDir = outcome.OutputDir;
            s_State.phase = Phase.Upload;
            Save();
        }

        static void StartUpload()
        {
            var state = s_State;
            DisposeClient();
            s_Client = new PortalClient(state.portalUrl, ApusCredentials.ClientId, ApusCredentials.ClientSecret, ApusCredentials.DevEmail);
            s_Cts = new CancellationTokenSource();
            var client = s_Client;
            var ct = s_Cts.Token;
            var project = ApusAssetPortSettings.instance.projectSlug;
            var groupId = state.groupId;
            var dir = state.outputDir;
            s_PendingGroupId = 0;
            s_LastRelease = null;
            s_Task = Task.Run(() => TimeoutAsError(async () =>
            {
                if (groupId <= 0)
                {
                    groupId = await Uploader.EnsureGroup(client, project, state.groupName, state.note, state.groupKey, ct).ConfigureAwait(false);
                    Interlocked.Exchange(ref s_PendingGroupId, groupId);
                }
                s_LastRelease = await Uploader.UploadRelease(client, groupId, dir, Progress, QueueLog, ct).ConfigureAwait(false);
            }, ct), ct);
            state.phase = Phase.Uploading;
            AddLog(state.CurrentPlatform + ": upload…");
            Save();
        }

        static void PollUpload()
        {
            var pending = Interlocked.Read(ref s_PendingGroupId);
            if (pending > 0 && s_State.groupId != pending)
            {
                s_State.groupId = pending;
                AddLog("Group #" + pending + ": " + Uploader.GroupUrl(s_State.portalUrl, pending));
                Save();
            }
            if (s_Task == null)
            {
                // Domain reload interrupted the upload: start this platform's upload again (release is replaced).
                s_State.phase = Phase.Upload;
                Save();
                return;
            }
            Changed?.Invoke();
            if (!s_Task.IsCompleted) return;
            var task = s_Task;
            s_Task = null;
            if (task.IsFaulted || task.IsCanceled)
            {
                Fail(s_State.CurrentPlatform + ": upload thất bại: " + Message(task));
                return;
            }
            var r = s_LastRelease;
            foreach (var w in r.Warnings) AddLog("⚠ " + w);
            s_State.summary.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} ({2}): release #{3}, upload {4} bundle ({5:0.0} MB), bỏ qua {6} bundle đã có",
                r.Platform, r.AppVersion, r.Kind, r.ReleaseId, r.Uploaded, r.BytesUploaded / 1048576.0, r.Skipped));
            AddLog(s_State.summary[s_State.summary.Count - 1]);
            s_State.index++;
            s_State.outputDir = null;
            s_State.phase = s_State.index < s_State.platforms.Count ? Phase.Switch : Phase.Complete;
            Save();
        }

        static void StartComplete()
        {
            if (s_Client == null)
                s_Client = new PortalClient(s_State.portalUrl, ApusCredentials.ClientId, ApusCredentials.ClientSecret, ApusCredentials.DevEmail);
            s_Cts = s_Cts ?? new CancellationTokenSource();
            var client = s_Client;
            var groupId = s_State.groupId;
            var ct = s_Cts.Token;
            Progress.SetStage("Chốt group #" + groupId);
            s_Task = Task.Run(() => TimeoutAsError(async () => { s_LastCompleteStatus = await Uploader.CompleteGroup(client, groupId, ct).ConfigureAwait(false); }, ct), ct);
            s_State.phase = Phase.Completing;
            Save();
        }

        static void PollComplete()
        {
            if (s_Task == null)
            {
                s_State.phase = Phase.Complete;
                Save();
                return;
            }
            if (!s_Task.IsCompleted) return;
            var task = s_Task;
            s_Task = null;
            if (task.IsFaulted || task.IsCanceled)
            {
                Fail("Chốt group thất bại: " + Message(task) + "\nMở group trên portal để xem chi tiết.");
                return;
            }
            AddLog("Đã chốt group #" + s_State.groupId + " (" + s_LastCompleteStatus + "). Chưa publish — publish trên portal.");
            s_State.phase = Phase.Done;
            Save();
            DisposeClient();
            EditorApplication.update -= Tick;
        }

        static void Fail(string message)
        {
            if (s_State == null) return;
            s_State.error = message;
            s_State.phase = Phase.Failed;
            AddLog("✖ " + message);
            Debug.LogError("[Apus] " + message);
            Save();
            DisposeClient();
            EditorApplication.update -= Tick;
        }

        /// <summary>
        /// Disposes the client only once its task has finished: disposing an HttpClient mid-request (e.g. on
        /// Cancel) would race the cancellation inside the upload task.
        /// </summary>
        static void DisposeClient()
        {
            var client = s_Client;
            var cts = s_Cts;
            var task = s_Task;
            s_Client = null;
            s_Cts = null;
            s_Task = null;
            if (task != null && !task.IsCompleted)
            {
                task.ContinueWith(_ =>
                {
                    client?.Dispose();
                    cts?.Dispose();
                }, TaskScheduler.Default);
                return;
            }
            client?.Dispose();
            cts?.Dispose();
        }

        /// <summary>
        /// An HttpClient timeout surfaces as OperationCanceledException, which would make the task look
        /// cancelled by the user: report it as a timeout instead (keeping the original as inner exception).
        /// </summary>
        static async Task TimeoutAsError(Func<Task> body, CancellationToken ct)
        {
            try
            {
                await body().ConfigureAwait(false);
            }
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Hết thời gian chờ phản hồi từ portal/storage (" + e.Message + ")", e);
            }
        }

        static string Message(Task task)
        {
            if (task.IsCanceled) return "đã huỷ";
            var e = task.Exception?.GetBaseException();
            return e == null ? "lỗi không rõ" : e.Message;
        }

        static BuildTarget ParseTarget(string platform)
        {
            switch (platform)
            {
                case "Android": return BuildTarget.Android;
                case "iOS": return BuildTarget.iOS;
                default: throw new ArgumentException("Platform không hỗ trợ: " + platform);
            }
        }

        static void QueueLog(string line)
        {
            lock (s_PendingLog) s_PendingLog.Add(line);
        }

        static void FlushPendingLog()
        {
            lock (s_PendingLog)
            {
                if (s_PendingLog.Count == 0) return;
                foreach (var line in s_PendingLog) AddLog(line);
                s_PendingLog.Clear();
            }
            Save();
        }

        static void AddLog(string line)
        {
            s_State.log.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
            if (s_State.log.Count > k_MaxLogLines) s_State.log.RemoveRange(0, s_State.log.Count - k_MaxLogLines);
            Debug.Log("[Apus] " + line);
        }

        static void Save()
        {
            if (s_State != null) SessionState.SetString(k_Key, JsonUtility.ToJson(s_State));
            Changed?.Invoke();
        }

        static State Load()
        {
            var json = SessionState.GetString(k_Key, "");
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonUtility.FromJson<State>(json);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
