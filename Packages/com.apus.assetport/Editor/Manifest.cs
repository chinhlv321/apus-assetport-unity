using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Apus.AssetPort
{
    /// <summary>Constants mirrored by hand from <c>shared/src/index.ts</c>.</summary>
    public static class PortalConstants
    {
        public static readonly string[] Platforms = { "Android", "iOS" };
        public const string KindBaseline = "baseline";
        public const string KindUpdate = "update";

        public const string ManifestFileName = "apus-manifest.json";
        public const int ManifestSchema = 1;

        public const string CatalogHashName = "catalog_content.hash";
        public static readonly string[] CatalogDataNames = { "catalog_content.json", "catalog_content.bin" };

        public const int PresignBatch = 500;
        public const int VerifyBatch = 200;
        public const int MaxFilesPerRelease = 20000;
        public const long LargeBundleWarnBytes = 200L * 1024 * 1024;

        public static bool IsPlatform(string v) => v != null && Platforms.Contains(v, StringComparer.Ordinal);

        public static bool IsCatalogFile(string name) =>
            name == CatalogHashName || CatalogDataNames.Contains(name, StringComparer.Ordinal);
    }

    [Serializable]
    public class ManifestFile
    {
        public string name;
        public long size;
        /// <summary>Lowercase hex.</summary>
        public string md5;
    }

    /// <summary>
    /// <c>apus-manifest.json</c>, matching the <c>Manifest</c> interface in shared/src/index.ts.
    /// </summary>
    [Serializable]
    public class ApusManifest
    {
        public int schema = PortalConstants.ManifestSchema;
        public string project;
        public string platform;
        public string appVersion;
        public string kind;
        public string unityVersion;
        public string addressablesVersion;
        /// <summary>Written as JSON null when empty.</summary>
        public string gitCommit;
        public string builtAt;
        public List<ManifestFile> files = new List<ManifestFile>();

        public long TotalBytes => files.Sum(f => f.size);

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema\": ").Append(schema.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"project\": ").Append(Json.Quote(project ?? "")).Append(",\n");
            sb.Append("  \"platform\": ").Append(Json.Quote(platform ?? "")).Append(",\n");
            sb.Append("  \"appVersion\": ").Append(Json.Quote(appVersion ?? "")).Append(",\n");
            sb.Append("  \"kind\": ").Append(Json.Quote(kind ?? "")).Append(",\n");
            sb.Append("  \"unityVersion\": ").Append(Json.Quote(unityVersion ?? "")).Append(",\n");
            sb.Append("  \"addressablesVersion\": ").Append(Json.Quote(addressablesVersion ?? "")).Append(",\n");
            sb.Append("  \"gitCommit\": ").Append(string.IsNullOrEmpty(gitCommit) ? "null" : Json.Quote(gitCommit)).Append(",\n");
            sb.Append("  \"builtAt\": ").Append(Json.Quote(builtAt ?? "")).Append(",\n");
            sb.Append("  \"files\": [");
            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    { \"name\": ").Append(Json.Quote(f.name))
                    .Append(", \"size\": ").Append(f.size.ToString(CultureInfo.InvariantCulture))
                    .Append(", \"md5\": ").Append(Json.Quote(f.md5)).Append(" }");
            }
            sb.Append(files.Count > 0 ? "\n  ]\n" : "]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        public static ApusManifest FromJson(string json)
        {
            var o = Json.AsObject(Json.Parse(json)) ?? throw new FormatException("Manifest is not a JSON object");
            var m = new ApusManifest
            {
                schema = (int)Json.GetLong(o, "schema", -1),
                project = Json.GetString(o, "project"),
                platform = Json.GetString(o, "platform"),
                appVersion = Json.GetString(o, "appVersion"),
                kind = Json.GetString(o, "kind"),
                unityVersion = Json.GetString(o, "unityVersion"),
                addressablesVersion = Json.GetString(o, "addressablesVersion"),
                gitCommit = Json.GetString(o, "gitCommit"),
                builtAt = Json.GetString(o, "builtAt"),
            };
            if (o.TryGetValue("files", out var filesObj) && filesObj is List<object> list)
            {
                foreach (var item in list)
                {
                    var fo = Json.AsObject(item);
                    m.files.Add(new ManifestFile
                    {
                        name = Json.GetString(fo, "name"),
                        size = Json.GetLong(fo, "size", -1),
                        md5 = Json.GetString(fo, "md5"),
                    });
                }
            }
            return m;
        }
    }

    /// <summary>Mirror of <c>validateManifest</c> and friends in shared/src/index.ts.</summary>
    public static class ManifestValidator
    {
        // JS `$` == .NET `\z` (no trailing-newline match).
        static readonly Regex k_AppVersion = new Regex(@"^[0-9A-Za-z._-]{1,32}\z", RegexOptions.CultureInvariant);
        static readonly Regex k_FileName = new Regex(@"^[\p{L}\p{N}_@+=()\[\]{}!~,-][\p{L}\p{M}\p{N}._@+=()\[\]{}!~, -]{0,199}\z", RegexOptions.CultureInvariant);
        static readonly Regex k_Slug = new Regex(@"^[a-z0-9][a-z0-9-]{1,39}\z", RegexOptions.CultureInvariant);
        static readonly Regex k_Md5 = new Regex(@"^[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

        public static bool IsValidAppVersion(string v) => v != null && k_AppVersion.IsMatch(v) && v != "." && v != "..";

        public static bool IsValidFileName(string v) => v != null && k_FileName.IsMatch(v) && !v.Contains("..");

        public static bool IsValidSlug(string v) => v != null && k_Slug.IsMatch(v);

        /// <summary>Returns human readable problems; empty means valid.</summary>
        public static List<string> Validate(ApusManifest m)
        {
            var errors = new List<string>();
            if (m == null)
            {
                errors.Add("Manifest không phải JSON object");
                return errors;
            }
            if (m.schema != PortalConstants.ManifestSchema) errors.Add("schema phải là " + PortalConstants.ManifestSchema);
            if (!IsValidSlug(m.project)) errors.Add("project không hợp lệ: '" + m.project + "' (slug a-z0-9-, 2–40 ký tự; đặt ở Project Settings > Apus Asset Port)");
            if (!PortalConstants.IsPlatform(m.platform)) errors.Add("platform phải là một trong " + string.Join(", ", PortalConstants.Platforms) + " (phân biệt hoa thường), đang là '" + m.platform + "'");
            if (!IsValidAppVersion(m.appVersion)) errors.Add("appVersion chỉ gồm [0-9A-Za-z._-], tối đa 32 ký tự, đang là '" + m.appVersion + "'");
            if (m.kind != PortalConstants.KindBaseline && m.kind != PortalConstants.KindUpdate) errors.Add("kind phải là baseline hoặc update");
            if (string.IsNullOrEmpty(m.unityVersion)) errors.Add("thiếu unityVersion");
            if (string.IsNullOrEmpty(m.addressablesVersion)) errors.Add("thiếu addressablesVersion");
            if (m.files == null)
            {
                errors.Add("thiếu danh sách files");
                return errors;
            }
            if (m.files.Count > PortalConstants.MaxFilesPerRelease) errors.Add("quá " + PortalConstants.MaxFilesPerRelease + " file");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in m.files)
            {
                if (f == null || !IsValidFileName(f.name))
                {
                    errors.Add("tên file không hợp lệ: " + f?.name);
                    continue;
                }
                if (!seen.Add(f.name)) errors.Add("file trùng: " + f.name);
                if (f.size < 0) errors.Add("size sai: " + f.name);
                if (f.md5 == null || !k_Md5.IsMatch(f.md5)) errors.Add("md5 sai: " + f.name);
                if (f.name == PortalConstants.ManifestFileName) errors.Add("manifest không được tự liệt kê chính nó");
            }
            if (!seen.Contains(PortalConstants.CatalogHashName))
                errors.Add("thiếu " + PortalConstants.CatalogHashName + " (kiểm tra Player Version Override = content)");
            var dataCount = PortalConstants.CatalogDataNames.Count(seen.Contains);
            if (dataCount != 1) errors.Add("cần đúng 1 file " + string.Join(" hoặc ", PortalConstants.CatalogDataNames));
            return errors;
        }
    }
}
