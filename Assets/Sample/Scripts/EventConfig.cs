using UnityEngine;

namespace Apus.Sample
{
    /// <summary>
    /// Live-ops content: changes without a new app build. Lives in the remote group <c>Event</c>; edit it, build a
    /// Content Update, publish on the portal and the game picks it up on the next launch.
    /// </summary>
    [CreateAssetMenu(menuName = "Apus Sample/Event Config")]
    public class EventConfig : ScriptableObject
    {
        public string title = "Sự kiện mùa thu";
        [TextArea] public string message = "Đăng nhập mỗi ngày để nhận quà.";
        public Sprite banner;
        public Color accent = new Color(1f, 0.6f, 0.2f);
        [Tooltip("Tăng mỗi lần đẩy content để dễ thấy bản nào đang chạy.")]
        public int revision = 1;
    }
}
