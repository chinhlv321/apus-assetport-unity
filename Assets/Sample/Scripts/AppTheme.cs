using UnityEngine;

namespace Apus.Sample
{
    /// <summary>Look of the app. Lives in the local group <c>Boot</c> (shipped with the player, Prevent Updates).</summary>
    [CreateAssetMenu(menuName = "Apus Sample/App Theme")]
    public class AppTheme : ScriptableObject
    {
        public Color background = new Color(0.08f, 0.09f, 0.13f);
        public Color panel = new Color(0.14f, 0.16f, 0.22f);
        public Color text = Color.white;
        public Color accent = new Color(0.27f, 0.55f, 1f);
    }
}
