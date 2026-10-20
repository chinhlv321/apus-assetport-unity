using Apus.Sample;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Helpers for trying the content-update workflow with the sample (README section 4).</summary>
public static class SampleMenu
{
    const string EventConfigPath = "Assets/Sample/Content/Event/EventConfig.asset";
    const string BootScene = "Assets/Sample/Scenes/Boot.unity";

    static readonly string[] Titles = { "Sự kiện mùa thu", "Lễ hội đèn lồng", "Tuần lễ vàng", "Săn kho báu" };

    /// <summary>
    /// Edits the live-ops asset in the remote group <c>Event</c> (title, colour, revision). Then build a
    /// <b>Content Update</b> (Apus > Build &amp; Upload…) and publish the new group on the portal.
    /// </summary>
    [MenuItem("Apus Sample/Simulate Event Update", priority = 0)]
    public static void SimulateEventUpdate()
    {
        var config = AssetDatabase.LoadAssetAtPath<EventConfig>(EventConfigPath);
        if (config == null)
        {
            Debug.LogError("[Sample] Không tìm thấy " + EventConfigPath);
            return;
        }
        Undo.RecordObject(config, "Simulate Event Update");
        config.revision++;
        config.title = Titles[config.revision % Titles.Length];
        config.message = "Bản content #" + config.revision + " — đẩy lên lúc " + System.DateTime.Now.ToString("HH:mm dd/MM", System.Globalization.CultureInfo.InvariantCulture) + ", không cần build lại app.";
        config.accent = Color.HSVToRGB(config.revision * 0.17f % 1f, 0.65f, 1f);
        EditorUtility.SetDirty(config);
        AssetDatabase.SaveAssets();
        Debug.Log("[Sample] EventConfig → revision " + config.revision + " (\"" + config.title + "\"). Tiếp theo: Apus > Build & Upload…, kiểu Content Update.");
        Selection.activeObject = config;
    }

    [MenuItem("Apus Sample/Open Boot Scene", priority = 20)]
    public static void OpenBootScene()
    {
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(BootScene);
    }
}
