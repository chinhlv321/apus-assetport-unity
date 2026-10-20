using UnityEngine;

namespace Apus.Sample
{
    /// <summary>An on-demand pack (remote group <c>Pack1</c>, label <c>pack1</c>), downloaded only when the player asks.</summary>
    [CreateAssetMenu(menuName = "Apus Sample/Item Pack")]
    public class ItemPack : ScriptableObject
    {
        public string displayName = "Gói vật phẩm 1";
        public Sprite[] items = new Sprite[0];
    }
}
