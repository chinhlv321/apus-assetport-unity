using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Apus.AssetPort
{
    public class PortalException : Exception
    {
        public readonly int Status;

        public PortalException(int status, string message) : base(message)
        {
            Status = status;
        }
    }

    /// <summary>One entry of <c>PresignResponse.uploads</c>.</summary>
    public class PresignUpload
    {
        public string Name;
        public string Url;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        /// <summary>When the portal signed this URL (UTC); presigned URLs expire after 1 hour.</summary>
        public DateTime IssuedAtUtc = DateTime.UtcNow;
    }

    public class PresignResult
    {
        public readonly List<PresignUpload> Uploads = new List<PresignUpload>();
        public readonly List<string> Skipped = new List<string>();
        public readonly List<string> Conflicts = new List<string>();
    }

    public class VerifyResult
    {
        public List<string> Verified = new List<string>();
        public List<string> Missing = new List<string>();
        public List<string> Mismatched = new List<string>();
    }

    /// <summary>
    /// HTTP client for the portal API (docs/api.md). Thread-safe; all methods can run off the main thread
    /// (no Unity API is used here).
    /// </summary>
    public sealed class PortalClient : IDisposable
    {
        readonly Uri m_Base;
        readonly HttpClient m_Api;
        readonly HttpClient m_Upload;
        readonly KeyValuePair<string, string>[] m_AuthHeaders;

        public string BaseUrl => m_Base.ToString().TrimEnd('/');

        public PortalClient(string portalUrl, string clientId, string clientSecret, string devEmail)
        {
            m_Base = new Uri(ApusCredentials.NormalizeUrl(portalUrl) + "/", UriKind.Absolute);

            var auth = new List<KeyValuePair<string, string>>();
            var local = ApusCredentials.IsLocalhost(m_Base.ToString());
            if (local && !string.IsNullOrEmpty(devEmail))
            {
                auth.Add(new KeyValuePair<string, string>("X-Dev-Email", devEmail));
            }
            else if (!string.IsNullOrEmpty(clientId))
            {
                auth.Add(new KeyValuePair<string, string>("CF-Access-Client-Id", clientId));
                auth.Add(new KeyValuePair<string, string>("CF-Access-Client-Secret", clientSecret ?? ""));
                // wrangler dev has no Access in front: act as this service token.
                if (local) auth.Add(new KeyValuePair<string, string>("X-Dev-Service-Token", clientId));
            }
            m_AuthHeaders = auth.ToArray();

            // Access answers a bad/missing token with a redirect to its login page: surface that instead of following it.
            m_Api = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
            m_Upload = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromHours(2) };
            m_Upload.DefaultRequestHeaders.ExpectContinue = false;
            m_Api.DefaultRequestHeaders.ExpectContinue = false;
        }

        public void Dispose()
        {
            m_Api.Dispose();
            m_Upload.Dispose();
        }

        // ---- API --------------------------------------------------------------------------

        public async Task<Dictionary<string, object>> GetMe(CancellationToken ct)
        {
            return Json.AsObject(await Send(HttpMethod.Get, "api/me", (string)null, ct).ConfigureAwait(false));
        }

        /// <summary>POST /groups — reuses a draft group with the same <paramref name="groupKey"/>.</summary>
        public async Task<Dictionary<string, object>> CreateGroup(string project, string name, string note, string groupKey, CancellationToken ct)
        {
            var body = new Dictionary<string, object> { ["project"] = project, ["name"] = name };
            if (!string.IsNullOrEmpty(note)) body["note"] = note;
            if (!string.IsNullOrEmpty(groupKey)) body["groupKey"] = groupKey;
            return Json.AsObject(await Send(HttpMethod.Post, "api/groups", Json.Serialize(body), ct).ConfigureAwait(false));
        }

        /// <summary>POST /groups/:id/releases with the manifest JSON exactly as written to disk.</summary>
        public async Task<Dictionary<string, object>> AddRelease(long groupId, string manifestJson, CancellationToken ct)
        {
            var body = "{\"manifest\":" + manifestJson.Trim() + "}";
            return Json.AsObject(await Send(HttpMethod.Post, "api/groups/" + groupId + "/releases", body, ct).ConfigureAwait(false));
        }

        public async Task<PresignResult> Presign(long releaseId, IList<string> names, CancellationToken ct)
        {
            var body = Json.Serialize(new Dictionary<string, object> { ["names"] = names });
            var o = Json.AsObject(await Send(HttpMethod.Post, "api/releases/" + releaseId + "/presign", body, ct).ConfigureAwait(false));
            var result = new PresignResult();
            result.Skipped.AddRange(Json.GetStringList(o, "skipped"));
            if (o != null && o.TryGetValue("uploads", out var uploads) && uploads is List<object> list)
            {
                foreach (var item in list)
                {
                    var u = Json.AsObject(item);
                    var upload = new PresignUpload { Name = Json.GetString(u, "name"), Url = Json.GetString(u, "url") };
                    if (u != null && u.TryGetValue("headers", out var h) && h is Dictionary<string, object> headers)
                        foreach (var kv in headers)
                            upload.Headers[kv.Key] = Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
                    result.Uploads.Add(upload);
                }
            }
            if (o != null && o.TryGetValue("conflicts", out var conflicts) && conflicts is List<object> clist)
            {
                foreach (var item in clist)
                {
                    var c = Json.AsObject(item);
                    result.Conflicts.Add(Json.GetString(c, "name") + " (đã có md5 " + Json.GetString(c, "existingMd5") + ", bản này " + Json.GetString(c, "md5") + ")");
                }
            }
            return result;
        }

        public async Task UploadCatalog(long releaseId, string fileName, string path, CancellationToken ct)
        {
            var bytes = File.ReadAllBytes(path);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            await Send(HttpMethod.Put, "api/releases/" + releaseId + "/catalog/" + Uri.EscapeDataString(fileName), content, ct).ConfigureAwait(false);
        }

        public async Task<VerifyResult> Verify(long releaseId, IList<string> names, CancellationToken ct)
        {
            var body = Json.Serialize(new Dictionary<string, object> { ["names"] = names });
            var o = Json.AsObject(await Send(HttpMethod.Post, "api/releases/" + releaseId + "/verify", body, ct).ConfigureAwait(false));
            return new VerifyResult
            {
                Verified = Json.GetStringList(o, "verified"),
                Missing = Json.GetStringList(o, "missing"),
                Mismatched = Json.GetStringList(o, "mismatched"),
            };
        }

        /// <summary>GET /groups/:id → <c>GroupInfo</c>.</summary>
        public async Task<Dictionary<string, object>> GetGroup(long groupId, CancellationToken ct)
        {
            return Json.AsObject(await Send(HttpMethod.Get, "api/groups/" + groupId, (string)null, ct).ConfigureAwait(false));
        }

        public async Task<Dictionary<string, object>> CompleteGroup(long groupId, CancellationToken ct)
        {
            return Json.AsObject(await Send(HttpMethod.Post, "api/groups/" + groupId + "/complete", "{}", ct).ConfigureAwait(false));
        }

        /// <summary>
        /// PUT one file to a presigned URL with exactly the returned headers. Relative URLs (dev proxy) resolve
        /// against the portal and carry the portal auth headers; absolute URLs elsewhere (R2) never do.
        /// </summary>
        /// <remarks>
        /// HTTP 412 means the object already exists (the presign headers carry <c>If-None-Match: *</c>); it is
        /// thrown as a <see cref="PortalException"/> with status 412 and the caller decides (verify compares MD5).
        /// </remarks>
        public async Task PutPresigned(PresignUpload upload, string path, CancellationToken ct)
        {
            var url = ResolveUploadUrl(upload.Url);
            var samePortal = string.Equals(url.GetLeftPart(UriPartial.Authority), m_Base.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
            using (var request = new HttpRequestMessage(HttpMethod.Put, url))
            {
                // Explicit Content-Length: never chunked (R2 presigned PUT rejects chunked bodies) and lets the
                // Mono handler stream instead of buffering to compute the length (see README, "Bộ nhớ khi upload").
                var content = new StreamContent(stream, 1 << 16);
                content.Headers.ContentLength = stream.Length;
                request.Content = content;
                foreach (var kv in upload.Headers)
                {
                    if (string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) || string.Equals(kv.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!request.Headers.TryAddWithoutValidation(kv.Key, kv.Value))
                        request.Content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
                if (samePortal) AddAuth(request);
                using (var response = await m_Upload.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new PortalException((int)response.StatusCode, "PUT " + upload.Name + " → HTTP " + (int)response.StatusCode + ": " + Truncate(text, 400));
                    }
                }
            }
        }

        /// <summary>True for an absolute http(s) URL (R2); false for a path relative to the portal (dev proxy).</summary>
        public static bool IsAbsoluteHttpUrl(string url) =>
            // UriKind.Absolute alone is not enough: on macOS/Linux Mono parses "/api/..." as file:///api/...
            Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

        /// <summary>
        /// Absolute presigned URLs are parsed on their own, never combined with the portal base. System.Uri keeps
        /// existing %XX escapes as-is in <see cref="Uri.AbsoluteUri"/> (what HttpClient sends) and only re-encodes
        /// characters that are not legal in a URI; a SigV4 signer already percent-encodes everything except the
        /// unreserved set (A-Z a-z 0-9 - . _ ~), so the path and query that reach R2 match what was signed.
        /// Only relative URLs (dev proxy) are resolved against the portal.
        /// </summary>
        Uri ResolveUploadUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) throw new PortalException(0, "Presign trả về URL rỗng.");
            if (IsAbsoluteHttpUrl(url)) return new Uri(url, UriKind.Absolute);
            return new Uri(m_Base, url);
        }

        // ---- plumbing ---------------------------------------------------------------------

        Task<object> Send(HttpMethod method, string path, string jsonBody, CancellationToken ct)
        {
            HttpContent content = null;
            if (jsonBody != null)
                content = new StringContent(jsonBody, new UTF8Encoding(false), "application/json");
            return Send(method, path, content, ct);
        }

        async Task<object> Send(HttpMethod method, string path, HttpContent content, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(method, new Uri(m_Base, path)))
            {
                request.Content = content;
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                AddAuth(request);
                using (var response = await m_Api.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    var what = method.Method + " /" + path;
                    if (status >= 300 && status < 400)
                        throw new PortalException(status, what + " bị chuyển hướng (HTTP " + status + " → " + response.Headers.Location +
                                                          "). Cloudflare Access không nhận service token: kiểm tra Client ID/Secret và policy Service Auth.");
                    if (status == (int)HttpStatusCode.Unauthorized || status == (int)HttpStatusCode.Forbidden)
                        throw new PortalException(status, what + " → HTTP " + status + ": " + ErrorText(text) + " (kiểm tra service token đã đăng ký cho project này chưa).");
                    if (!response.IsSuccessStatusCode)
                        throw new PortalException(status, what + " → HTTP " + status + ": " + ErrorText(text));
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    try
                    {
                        return Json.Parse(text);
                    }
                    catch (FormatException)
                    {
                        throw new PortalException(status, what + " trả về không phải JSON (có thể là trang đăng nhập của Access): " + Truncate(text, 200));
                    }
                }
            }
        }

        void AddAuth(HttpRequestMessage request)
        {
            foreach (var kv in m_AuthHeaders) request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        static string ErrorText(string body)
        {
            try
            {
                var o = Json.AsObject(Json.Parse(body));
                var error = Json.GetString(o, "error");
                if (error != null)
                {
                    if (o.TryGetValue("details", out var details) && details != null) error += " " + Json.Serialize(details);
                    return error;
                }
            }
            catch (Exception)
            {
                // not JSON
            }
            return Truncate(body, 400);
        }

        static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
