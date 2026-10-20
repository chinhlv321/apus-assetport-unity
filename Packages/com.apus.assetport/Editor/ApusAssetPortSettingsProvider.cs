using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Apus.AssetPort
{
    static class ApusAssetPortSettingsProvider
    {
        public const string Path = "Project/Apus Asset Port";

        [SettingsProvider]
        static SettingsProvider Create()
        {
            return new SettingsProvider(Path, SettingsScope.Project)
            {
                label = "Apus Asset Port",
                keywords = new HashSet<string>(new[] { "Apus", "Addressables", "R2", "manifest", "portal" }),
                guiHandler = _ => Draw(),
            };
        }

        static void Draw()
        {
            var s = ApusAssetPortSettings.instance;
            EditorGUIUtility.labelWidth = 180;
            EditorGUI.BeginChangeCheck();
            s.projectSlug = EditorGUILayout.TextField(new GUIContent("Project slug", "Slug của project trên portal"), s.projectSlug).Trim();
            s.portalUrl = EditorGUILayout.TextField(new GUIContent("Portal URL", "https://admin.<domain> hoặc http://127.0.0.1:8787 khi chạy wrangler dev"), s.portalUrl).Trim();
            s.generateManifest = EditorGUILayout.Toggle(new GUIContent("Generate manifest", "Ghi apus-manifest.json sau mỗi lần build Addressables"), s.generateManifest);
            if (EditorGUI.EndChangeCheck()) s.SaveSettings();

            if (!string.IsNullOrEmpty(s.projectSlug) && !ManifestValidator.IsValidSlug(s.projectSlug))
                EditorGUILayout.HelpBox("Slug chỉ gồm a-z, 0-9, '-', 2–40 ký tự.", MessageType.Error);
            if (ApusCredentials.PortalUrlFromEnv)
                EditorGUILayout.HelpBox("Biến môi trường APUS_PORTAL_URL đang ghi đè Portal URL: " + ApusCredentials.PortalUrl, MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Access service token (Client ID/Secret) lưu trong EditorPrefs của máy này, không lưu trong project. " +
                "Nhập ở cửa sổ Apus > Build & Upload. CI dùng env APUS_PORTAL_URL, APUS_CLIENT_ID, APUS_CLIENT_SECRET.",
                MessageType.None);

            if (!string.IsNullOrEmpty(s.cdnBaseUrl))
                EditorGUILayout.LabelField("CDN / envPaths", s.cdnBaseUrl + " · " + s.envPathDev + ", " + s.envPathStaging + ", prod", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Setup Addressables…")) AddressablesSetupWindow.Open();
            if (GUILayout.Button("Check Addressables Config")) ConfigChecker.RunAndLog(true);
            if (GUILayout.Button("Open Build & Upload")) BuildAndUploadWindow.Open();
            EditorGUILayout.EndHorizontal();
        }
    }
}
