using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Apus.AssetPort
{
    /// <summary>Writes an <see cref="ApusRemoteConfigDefaults"/> asset to the JSON file the portal's Remote config page imports.</summary>
    static class RemoteConfigDefaultsExport
    {
        [MenuItem("Apus/Export Remote Config Defaults…", priority = 23)]
        static void ExportMenu()
        {
            var asset = Selection.activeObject as ApusRemoteConfigDefaults;
            if (asset == null)
            {
                var found = AssetDatabase.FindAssets("t:" + nameof(ApusRemoteConfigDefaults))
                    .Select(g => AssetDatabase.LoadAssetAtPath<ApusRemoteConfigDefaults>(AssetDatabase.GUIDToAssetPath(g)))
                    .Where(a => a != null).ToList();
                if (found.Count == 0)
                {
                    EditorUtility.DisplayDialog("Apus", "Chưa có asset Remote Config Defaults. Tạo bằng Assets > Create > Apus > Remote Config Defaults.", "OK");
                    return;
                }
                if (found.Count > 1)
                {
                    EditorUtility.DisplayDialog("Apus", "Có " + found.Count + " asset Remote Config Defaults. Chọn asset cần export trong Project rồi chạy lại.", "OK");
                    return;
                }
                asset = found[0];
            }
            Export(asset);
        }

        [MenuItem("CONTEXT/ApusRemoteConfigDefaults/Export JSON…")]
        static void ExportContext(MenuCommand command) => Export((ApusRemoteConfigDefaults)command.context);

        public static void Export(ApusRemoteConfigDefaults asset)
        {
            var errors = new List<string>();
            var json = asset.ToJson(errors);
            if (errors.Count > 0)
            {
                // Nothing half-exported: a file without the bad keys would silently drop them on the portal.
                EditorUtility.DisplayDialog("Apus", "Sửa các key sau rồi export lại:\n\n" + string.Join("\n", errors.Take(20)) +
                    (errors.Count > 20 ? "\n… +" + (errors.Count - 20) : ""), "OK");
                return;
            }
            var slug = ApusAssetPortSettings.instance.projectSlug;
            var path = EditorUtility.SaveFilePanel("Export Remote Config", "", (string.IsNullOrEmpty(slug) ? asset.name : slug) + "-remote-config.json", "json");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllText(path, json);
            Debug.Log("[Apus] Exported " + asset.entries.Count + " keys to " + path + ". Import it on the portal: Remote config > Import JSON.");
            EditorUtility.RevealInFinder(path);
        }
    }
}
