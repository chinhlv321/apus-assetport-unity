using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Apus.AssetPort
{
    /// <summary>Progress shared between the upload task and the UI thread.</summary>
    public class UploadProgress
    {
        readonly object m_Lock = new object();
        string m_Stage = "";
        string m_Current = "";
        long m_BytesDone, m_BytesTotal;
        int m_FilesDone, m_FilesTotal;

        public void SetStage(string stage)
        {
            lock (m_Lock) m_Stage = stage;
        }

        public void Begin(int files, long bytes)
        {
            lock (m_Lock)
            {
                m_FilesTotal = files;
                m_BytesTotal = bytes;
                m_FilesDone = 0;
                m_BytesDone = 0;
            }
        }

        public void SetCurrent(string name)
        {
            lock (m_Lock) m_Current = name;
        }

        public void FileDone(long bytes)
        {
            lock (m_Lock)
            {
                m_FilesDone++;
                m_BytesDone += bytes;
            }
        }

        public void Read(out string stage, out string current, out float fraction, out string counts)
        {
            lock (m_Lock)
            {
                stage = m_Stage;
                current = m_Current;
                fraction = m_BytesTotal > 0 ? (float)((double)m_BytesDone / m_BytesTotal) : (m_FilesTotal > 0 ? (float)m_FilesDone / m_FilesTotal : 0f);
                counts = string.Format(CultureInfo.InvariantCulture, "{0}/{1} file, {2:0.0}/{3:0.0} MB",
                    m_FilesDone, m_FilesTotal, m_BytesDone / 1048576.0, m_BytesTotal / 1048576.0);
            }
        }
    }

    public class ReleaseUploadResult
    {
        public long ReleaseId;
        public string Platform;
        public string AppVersion;
        public string Kind;
        public int Uploaded;
        public int Skipped;
        public long BytesUploaded;
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>Upload flow from docs/api.md, steps 2–6. Runs off the main thread (no Unity API).</summary>
    public static class Uploader
    {
        const int k_Parallel = 4;
        const int k_Retries = 3;
        /// <summary>Presigned URLs live 1 hour; re-sign before using one older than this (same as the web engine).</summary>
        static readonly TimeSpan k_UrlMaxAge = TimeSpan.FromMinutes(45);
        /// <summary>How many times one file may be re-signed after R2 answers 403 (expired/invalid signature).</summary>
        const int k_MaxResign = 2;

        enum PutOutcome { Uploaded, AlreadyExists }

        sealed class PutCounters
        {
            public int Uploaded;
            public int Existed;
            public long Bytes;
            public Exception FirstError;
        }

        public static string GroupUrl(string portalUrl, long groupId) => ApusCredentials.NormalizeUrl(portalUrl) + "/groups/" + groupId;

        /// <summary>Step 1: create the draft group, or reuse the draft with the same key.</summary>
        public static async Task<long> EnsureGroup(PortalClient client, string project, string name, string note, string groupKey, CancellationToken ct)
        {
            // Retrying is only safe with a groupKey: the portal then reuses the draft created by a lost attempt.
            var group = string.IsNullOrEmpty(groupKey)
                ? await client.CreateGroup(project, name, note, groupKey, ct).ConfigureAwait(false)
                : await WithRetryValue(() => client.CreateGroup(project, name, note, groupKey, ct), ct).ConfigureAwait(false);
            var id = Json.GetLong(group, "id", -1);
            if (id <= 0) throw new PortalException(0, "POST /api/groups không trả về id.");
            var status = Json.GetString(group, "status");
            if (status != null && status != "draft")
                throw new PortalException(0, "Group " + id + " đã ở trạng thái '" + status + "', không thêm release được. Dùng groupKey khác.");
            return id;
        }

        /// <summary>Steps 2–5 for one platform folder containing apus-manifest.json.</summary>
        public static async Task<ReleaseUploadResult> UploadRelease(PortalClient client, long groupId, string dir, UploadProgress progress, Action<string> log, CancellationToken ct)
        {
            var manifestPath = Path.Combine(dir, PortalConstants.ManifestFileName);
            if (!File.Exists(manifestPath)) throw new InvalidOperationException("Không có " + manifestPath);
            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = ApusManifest.FromJson(manifestJson);
            var problems = ManifestValidator.Validate(manifest);
            if (problems.Count > 0) throw new InvalidOperationException("Manifest không hợp lệ:\n" + string.Join("\n", problems));

            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in manifest.files)
            {
                var p = Path.Combine(dir, f.name);
                if (!File.Exists(p)) throw new InvalidOperationException("Thiếu file trong manifest: " + p);
                var size = new FileInfo(p).Length;
                if (size != f.size) throw new InvalidOperationException("File " + f.name + " có size " + size + " khác manifest (" + f.size + "). Build lại.");
                paths[f.name] = p;
            }
            var extra = Directory.GetFiles(dir).Select(Path.GetFileName)
                .Where(n => n != PortalConstants.ManifestFileName && !paths.ContainsKey(n)).ToList();

            var result = new ReleaseUploadResult { Platform = manifest.platform, AppVersion = manifest.appVersion, Kind = manifest.kind };
            if (extra.Count > 0) result.Warnings.Add("Bỏ qua " + extra.Count + " file không có trong manifest: " + string.Join(", ", extra.Take(10)) + (extra.Count > 10 ? ", …" : ""));

            // Step 2 (idempotent: replaces a draft release of the same platform + appVersion in this group)
            progress.SetStage(manifest.platform + ": tạo release");
            var added = await WithRetryValue(() => client.AddRelease(groupId, manifestJson, ct), ct).ConfigureAwait(false);
            var release = Json.AsObject(added != null && added.TryGetValue("release", out var r) ? r : null);
            result.ReleaseId = Json.GetLong(release, "id", -1);
            if (result.ReleaseId <= 0) throw new PortalException(0, "POST /groups/" + groupId + "/releases không trả về release.id");
            result.Warnings.AddRange(Json.GetStringList(added, "warnings"));
            log?.Invoke(manifest.platform + " " + manifest.appVersion + " (" + manifest.kind + "): release #" + result.ReleaseId);

            // Step 3: presign each batch right before uploading it, so no URL waits behind earlier batches.
            var releaseId = result.ReleaseId;
            var bundleNames = manifest.files.Where(f => !PortalConstants.IsCatalogFile(f.name)).Select(f => f.name).ToList();
            var sizes = manifest.files.ToDictionary(f => f.name, f => f.size, StringComparer.Ordinal);
            var catalogFiles = manifest.files.Where(f => PortalConstants.IsCatalogFile(f.name))
                .OrderBy(f => f.name == PortalConstants.CatalogHashName ? 1 : 0).ToList();
            progress.Begin(manifest.files.Count, manifest.TotalBytes);
            var toVerify = new List<string>();
            var existed = 0;
            var batchIndex = 0;
            var batchCount = (bundleNames.Count + PortalConstants.PresignBatch - 1) / PortalConstants.PresignBatch;
            foreach (var batch in Batches(bundleNames, PortalConstants.PresignBatch))
            {
                batchIndex++;
                var part = batchCount > 1 ? " (đợt " + batchIndex + "/" + batchCount + ")" : "";
                progress.SetStage(manifest.platform + ": xin presigned URL" + part);
                var presign = await PresignChecked(client, releaseId, batch, ct).ConfigureAwait(false);
                result.Skipped += presign.Skipped.Count;
                foreach (var name in presign.Skipped) progress.FileDone(sizes[name]);

                progress.SetStage(manifest.platform + ": upload " + presign.Uploads.Count + " bundle" + part + " (bỏ qua " + result.Skipped + " bundle đã có)");
                var counters = await PutAll(client, releaseId, presign.Uploads, paths, sizes, progress, ct).ConfigureAwait(false);
                result.Uploaded += counters.Uploaded;
                result.BytesUploaded += counters.Bytes;
                existed += counters.Existed;
                // Files that answered 412 (already in storage) are verified too: verify decides match vs mismatch.
                toVerify.AddRange(presign.Uploads.Select(u => u.Name));
            }
            result.Skipped += existed;
            if (existed > 0) log?.Invoke(manifest.platform + ": " + existed + " bundle đã có sẵn trên storage (HTTP 412), sẽ kiểm tra MD5 ở bước verify.");

            // Step 4: catalog data first, .hash last.
            progress.SetStage(manifest.platform + ": upload catalog");
            foreach (var f in catalogFiles)
            {
                progress.SetCurrent(f.name);
                await WithRetry(() => client.UploadCatalog(releaseId, f.name, paths[f.name], ct), ct).ConfigureAwait(false);
                progress.FileDone(f.size);
            }

            // Step 5: verify, one re-upload round for missing/mismatched.
            progress.SetStage(manifest.platform + ": verify");
            var bad = await VerifyAll(client, releaseId, toVerify, ct).ConfigureAwait(false);
            if (bad.Count > 0)
            {
                log?.Invoke(manifest.platform + ": " + bad.Count + " bundle chưa verify được, upload lại: " + string.Join(", ", bad.Take(10)));
                var retryNames = new List<string>();
                foreach (var batch in Batches(bad, PortalConstants.PresignBatch))
                {
                    var presign = await PresignChecked(client, releaseId, batch, ct).ConfigureAwait(false);
                    progress.Begin(presign.Uploads.Count, presign.Uploads.Sum(u => sizes[u.Name]));
                    await PutAll(client, releaseId, presign.Uploads, paths, sizes, progress, ct).ConfigureAwait(false);
                    retryNames.AddRange(presign.Uploads.Select(u => u.Name));
                    // A name now reported as skipped is still re-verified below.
                    retryNames.AddRange(presign.Skipped);
                }
                var stillBad = await VerifyAll(client, releaseId, retryNames, ct).ConfigureAwait(false);
                if (stillBad.Count > 0)
                    throw new PortalException(0, "Verify thất bại sau khi upload lại (file trên storage khác MD5 hoặc vẫn thiếu): " + string.Join(", ", stillBad));
            }
            return result;
        }

        /// <summary>Step 6.</summary>
        public static async Task<string> CompleteGroup(PortalClient client, long groupId, CancellationToken ct)
        {
            var attempts = 0;
            Dictionary<string, object> response;
            try
            {
                response = await WithRetryValue(() =>
                {
                    attempts++;
                    return client.CompleteGroup(groupId, ct);
                }, ct).ConfigureAwait(false);
            }
            catch (PortalException e) when (attempts > 1 && e.Status >= 400 && e.Status < 500)
            {
                // An earlier attempt may have succeeded with the response lost: trust the group's state.
                var group = await WithRetryValue(() => client.GetGroup(groupId, ct), ct).ConfigureAwait(false);
                var status = Json.GetString(group, "status");
                if (status != null && status != "draft") return status;
                throw;
            }
            var g = Json.AsObject(response != null && response.TryGetValue("group", out var o) ? o : null);
            return Json.GetString(g, "status");
        }

        /// <summary>Presign with retry, then check the answer against the request (every name answered once, no strangers).</summary>
        static async Task<PresignResult> PresignChecked(PortalClient client, long releaseId, List<string> names, CancellationToken ct)
        {
            var presign = await WithRetryValue(() => client.Presign(releaseId, names, ct), ct).ConfigureAwait(false);
            if (presign.Conflicts.Count > 0)
                throw new PortalException(409, "Bundle trùng tên nhưng khác MD5 (build không ổn định giữa các máy?), dừng upload:\n" + string.Join("\n", presign.Conflicts));
            var requested = new HashSet<string>(names, StringComparer.Ordinal);
            var answered = new HashSet<string>(StringComparer.Ordinal);
            var strangers = new List<string>();
            foreach (var name in presign.Uploads.Select(u => u.Name).Concat(presign.Skipped))
            {
                if (name == null || !requested.Contains(name) || !answered.Add(name)) strangers.Add(name ?? "(null)");
            }
            if (strangers.Count > 0)
                throw new PortalException(0, "Portal trả về presign cho tên file không có trong yêu cầu (hoặc bị lặp): " + string.Join(", ", strangers.Take(10)) +
                                             ". Có thể portal và package lệch phiên bản.");
            var missing = names.Where(n => !answered.Contains(n)).ToList();
            if (missing.Count > 0)
                throw new PortalException(0, "Portal không trả về presigned URL cho " + missing.Count + " file: " + string.Join(", ", missing.Take(10)));
            var noUrl = presign.Uploads.Where(u => string.IsNullOrEmpty(u.Url)).Select(u => u.Name).ToList();
            if (noUrl.Count > 0) throw new PortalException(0, "Presign trả về URL rỗng cho: " + string.Join(", ", noUrl.Take(10)));
            return presign;
        }

        /// <summary>Fresh URL for one file, or null when the portal now reports it as already uploaded.</summary>
        static async Task<PresignUpload> Resign(PortalClient client, long releaseId, string name, CancellationToken ct)
        {
            var presign = await PresignChecked(client, releaseId, new List<string> { name }, ct).ConfigureAwait(false);
            return presign.Uploads.FirstOrDefault();
        }

        static async Task<PutOutcome> UploadOne(PortalClient client, long releaseId, PresignUpload upload, string path, CancellationToken ct)
        {
            var current = upload;
            for (var resigns = 0; ; resigns++)
            {
                if (DateTime.UtcNow - current.IssuedAtUtc > k_UrlMaxAge)
                {
                    current = await Resign(client, releaseId, current.Name, ct).ConfigureAwait(false);
                    if (current == null) return PutOutcome.AlreadyExists;
                }
                var attempt = current;
                try
                {
                    await WithRetry(() => client.PutPresigned(attempt, path, ct), ct).ConfigureAwait(false);
                    return PutOutcome.Uploaded;
                }
                catch (PortalException e) when (e.Status == 412)
                {
                    // If-None-Match: * — the object is already in storage; verify compares its MD5.
                    return PutOutcome.AlreadyExists;
                }
                catch (PortalException e) when (e.Status == 403 && resigns < k_MaxResign && PortalClient.IsAbsoluteHttpUrl(attempt.Url))
                {
                    // R2 answers 403 for an expired or otherwise rejected signature: sign again and retry.
                    current = await Resign(client, releaseId, attempt.Name, ct).ConfigureAwait(false);
                    if (current == null) return PutOutcome.AlreadyExists;
                }
            }
        }

        /// <summary>Parallel PUTs; the first failure cancels the others and is the exception reported.</summary>
        static async Task<PutCounters> PutAll(PortalClient client, long releaseId, List<PresignUpload> uploads, Dictionary<string, string> paths,
            Dictionary<string, long> sizes, UploadProgress progress, CancellationToken ct)
        {
            var counters = new PutCounters();
            if (uploads.Count == 0) return counters;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var gate = new SemaphoreSlim(k_Parallel))
            {
                var token = linked.Token;
                var tasks = uploads.Select(async u =>
                {
                    try
                    {
                        await gate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            if (!paths.TryGetValue(u.Name, out var path) || !sizes.TryGetValue(u.Name, out var size))
                                throw new InvalidOperationException("Portal trả về presigned URL cho file không có trong manifest: " + u.Name);
                            progress.SetCurrent(u.Name);
                            var outcome = await UploadOne(client, releaseId, u, path, token).ConfigureAwait(false);
                            if (outcome == PutOutcome.Uploaded)
                            {
                                Interlocked.Increment(ref counters.Uploaded);
                                Interlocked.Add(ref counters.Bytes, size);
                            }
                            else
                            {
                                Interlocked.Increment(ref counters.Existed);
                            }
                            progress.FileDone(size);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }
                    catch (Exception e)
                    {
                        // Sibling cancellations are a consequence, not the cause.
                        if (!(e is OperationCanceledException && token.IsCancellationRequested))
                            Interlocked.CompareExchange(ref counters.FirstError, e, null);
                        try
                        {
                            linked.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            // already finished
                        }
                        throw;
                    }
                }).ToList();
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    ct.ThrowIfCancellationRequested();
                    var first = Volatile.Read(ref counters.FirstError);
                    if (first != null) ExceptionDispatchInfo.Capture(first).Throw();
                    throw;
                }
            }
            return counters;
        }

        static async Task<List<string>> VerifyAll(PortalClient client, long releaseId, List<string> names, CancellationToken ct)
        {
            var bad = new List<string>();
            foreach (var batch in Batches(names.Distinct(StringComparer.Ordinal).ToList(), PortalConstants.VerifyBatch))
            {
                var v = await WithRetryValue(() => client.Verify(releaseId, batch, ct), ct).ConfigureAwait(false);
                bad.AddRange(v.Missing);
                bad.AddRange(v.Mismatched);
            }
            return bad.Distinct().ToList();
        }

        /// <summary>1 attempt + up to 3 retries with backoff, for network errors, timeouts, 5xx, 408 and 429 only.</summary>
        static async Task WithRetry(Func<Task> action, CancellationToken ct)
        {
            await WithRetryValue(async () =>
            {
                await action().ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }

        static async Task<T> WithRetryValue<T>(Func<Task<T>> action, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await action().ConfigureAwait(false);
                }
                catch (Exception e) when (attempt < k_Retries && !ct.IsCancellationRequested && Retryable(e))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
                }
            }
        }

        static bool Retryable(Exception e)
        {
            // 3xx (Access login redirect) and 4xx are answers, not glitches.
            if (e is PortalException pe) return pe.Status >= 500 || pe.Status == 408 || pe.Status == 429;
            if (e is FileNotFoundException || e is DirectoryNotFoundException || e is UnauthorizedAccessException) return false;
            // OperationCanceledException with the caller's token not cancelled = HttpClient timeout.
            return e is HttpRequestException || e is IOException || e is WebException || e is SocketException ||
                   e is TimeoutException || e is OperationCanceledException;
        }

        static IEnumerable<List<string>> Batches(List<string> items, int size)
        {
            for (var i = 0; i < items.Count; i += size) yield return items.GetRange(i, Math.Min(size, items.Count - i));
        }
    }
}
