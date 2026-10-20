using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace Apus.Sample
{
    /// <summary>
    /// The sample game's only screen (scene <c>Assets/Sample/Scenes/Boot.unity</c>). Startup order a real game follows:
    /// <list type="number">
    /// <item>Show the loading screen from <b>local</b> content (works offline, shipped in the app).</item>
    /// <item><see cref="ContentUpdater"/>: switch to the latest catalog published on the portal.</item>
    /// <item>Load <b>remote</b> live-ops content (<c>event/config</c>) — downloaded and cached automatically.</item>
    /// <item>Offer an <b>on-demand</b> pack (label <c>pack1</c>): show its size, download with progress, then load it.</item>
    /// </list>
    /// The UI is built in code so the scene stays a single GameObject; replace it with your own UI.
    /// </summary>
    public class SampleApp : MonoBehaviour
    {
        // Addresses and labels set on the entries in Window > Asset Management > Addressables > Groups.
        public const string ThemeAddress = "boot/theme";
        public const string LogoAddress = "boot/logo";
        public const string EventAddress = "event/config";
        public const string PackLabel = "pack1";
        public const string PackAddress = "pack1/items";

        Font m_Font;
        AppTheme m_Theme;
        Image m_Background, m_Logo, m_Banner;
        Text m_Status, m_EventTitle, m_EventMessage, m_PackStatus, m_Version;
        RectTransform m_Progress, m_ItemsRow;
        Button m_DownloadButton, m_ClearButton, m_RetryButton;
        AsyncOperationHandle<EventConfig> m_Event;
        AsyncOperationHandle<ItemPack> m_Pack;
        bool m_Busy;

        IEnumerator Start()
        {
            BuildUi();
            m_Version.text = "v" + Application.version + " · " + Application.platform;

            // 1. Local content: in the app, available even without network.
            var theme = Addressables.LoadAssetAsync<AppTheme>(ThemeAddress);
            var logo = Addressables.LoadAssetAsync<Sprite>(LogoAddress);
            yield return theme;
            yield return logo;
            if (theme.Status == AsyncOperationStatus.Succeeded) ApplyTheme(theme.Result);
            if (logo.Status == AsyncOperationStatus.Succeeded) m_Logo.sprite = logo.Result;
            // Kept loaded for the app's lifetime; release handles you no longer need.

            // 2. Catalog update.
            SetStatus("Đang kiểm tra content mới…");
            ContentUpdater.Result result = ContentUpdater.Result.Offline;
            yield return ContentUpdater.Run((r, _) => result = r);
            SetStatus(result == ContentUpdater.Result.Updated ? "Đã cập nhật content mới."
                : result == ContentUpdater.Result.UpToDate ? "Content đang là bản mới nhất."
                : "Không kết nối được CDN, dùng content đã lưu."); // details already logged by ContentUpdater

            // 3. Remote live-ops content.
            yield return LoadEvent();

            // 4. On-demand pack.
            yield return RefreshPackSize();
        }

        IEnumerator LoadEvent()
        {
            m_RetryButton.gameObject.SetActive(false);
            if (m_Event.IsValid()) Addressables.Release(m_Event);
            m_Event = Addressables.LoadAssetAsync<EventConfig>(EventAddress);
            yield return m_Event;
            if (m_Event.Status != AsyncOperationStatus.Succeeded)
            {
                m_EventTitle.text = "Chưa tải được sự kiện";
                m_EventMessage.text = "Lần đầu chạy cần có mạng. " + m_Event.OperationException?.GetBaseException().Message;
                Addressables.Release(m_Event);
                m_RetryButton.gameObject.SetActive(true);
                yield break;
            }
            var ev = m_Event.Result;
            m_EventTitle.text = ev.title + "  (rev " + ev.revision + ")";
            m_EventTitle.color = ev.accent;
            m_EventMessage.text = ev.message;
            m_Banner.sprite = ev.banner;
            m_Banner.color = ev.banner != null ? Color.white : Color.clear;
        }

        IEnumerator RefreshPackSize()
        {
            m_DownloadButton.interactable = false;
            long bytes = -1;
            string error = null;
            yield return ContentDownloader.GetDownloadSize(PackLabel, (b, e) => { bytes = b; error = e; });
            if (bytes < 0)
            {
                m_PackStatus.text = "Không lấy được dung lượng gói: " + error;
                m_DownloadButton.interactable = true;
                yield break;
            }
            m_PackStatus.text = bytes == 0 ? "Gói đã có trên máy." : "Cần tải " + FormatBytes(bytes) + ".";
            SetButtonLabel(m_DownloadButton, bytes == 0 ? "Mở gói" : "Tải gói (" + FormatBytes(bytes) + ")");
            m_DownloadButton.interactable = true;
        }

        IEnumerator DownloadAndShowPack()
        {
            if (m_Busy) yield break;
            m_Busy = true;
            m_DownloadButton.interactable = m_ClearButton.interactable = false;

            string error = null;
            yield return ContentDownloader.Download(PackLabel, SetProgress, e => error = e);
            if (error != null)
            {
                m_PackStatus.text = "Tải gói lỗi: " + error;
            }
            else
            {
                if (m_Pack.IsValid()) Addressables.Release(m_Pack);
                m_Pack = Addressables.LoadAssetAsync<ItemPack>(PackAddress);
                yield return m_Pack;
                if (m_Pack.Status == AsyncOperationStatus.Succeeded) ShowItems(m_Pack.Result);
                else m_PackStatus.text = "Không mở được gói: " + m_Pack.OperationException?.GetBaseException().Message;
            }
            m_Busy = false;
            m_ClearButton.interactable = true;
            yield return RefreshPackSize();
        }

        IEnumerator ClearPack()
        {
            if (m_Busy) yield break;
            m_Busy = true;
            // Loaded bundles cannot be removed from the cache: release them first.
            if (m_Pack.IsValid()) Addressables.Release(m_Pack);
            m_Pack = default;
            foreach (Transform child in m_ItemsRow) Destroy(child.gameObject);
            yield return ContentDownloader.ClearCache(PackLabel, () => { });
            SetProgress(0);
            m_Busy = false;
            yield return RefreshPackSize();
        }

        void ShowItems(ItemPack pack)
        {
            foreach (Transform child in m_ItemsRow) Destroy(child.gameObject);
            foreach (var sprite in pack.items)
            {
                var img = NewImage("Item", m_ItemsRow, Color.white);
                img.sprite = sprite;
                img.preserveAspect = true;
                img.gameObject.AddComponent<LayoutElement>().preferredWidth = 120;
            }
            m_PackStatus.text = pack.displayName + ": " + pack.items.Length + " vật phẩm.";
        }

        void OnDestroy()
        {
            if (m_Event.IsValid()) Addressables.Release(m_Event);
            if (m_Pack.IsValid()) Addressables.Release(m_Pack);
        }

        // ---------------------------------------------------------------- UI (plain uGUI, built in code)

        void BuildUi()
        {
            m_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#if UNITY_2023_1_OR_NEWER
            if (FindAnyObjectByType<EventSystem>() == null)
#else
            if (FindObjectOfType<EventSystem>() == null)
#endif
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            var root = (RectTransform)canvasGo.transform;

            m_Background = NewImage("Background", root, new Color(0.08f, 0.09f, 0.13f));
            Stretch(m_Background.rectTransform);
            var column = NewVertical("Column", m_Background.rectTransform, 28, new RectOffset(60, 60, 120, 80));
            Stretch(column);

            m_Logo = NewImage("Logo", column, Color.white);
            m_Logo.preserveAspect = true;
            Size(m_Logo.gameObject, 260);
            m_Status = NewText("Status", column, 34, TextAnchor.MiddleCenter);
            m_Status.text = "Đang khởi động…";

            var eventPanel = NewVertical("EventPanel", column, 16, new RectOffset(32, 32, 32, 32));
            eventPanel.gameObject.AddComponent<Image>().color = new Color(0.14f, 0.16f, 0.22f);
            m_Banner = NewImage("Banner", eventPanel, Color.clear);
            m_Banner.preserveAspect = true;
            Size(m_Banner.gameObject, 360);
            m_EventTitle = NewText("EventTitle", eventPanel, 48, TextAnchor.MiddleLeft);
            m_EventTitle.fontStyle = FontStyle.Bold;
            m_EventMessage = NewText("EventMessage", eventPanel, 34, TextAnchor.UpperLeft);
            m_RetryButton = NewButton("Retry", eventPanel, "Thử lại", () => StartCoroutine(LoadEvent()));
            m_RetryButton.gameObject.SetActive(false);

            var packPanel = NewVertical("PackPanel", column, 16, new RectOffset(32, 32, 32, 32));
            packPanel.gameObject.AddComponent<Image>().color = new Color(0.14f, 0.16f, 0.22f);
            var packTitle = NewText("PackTitle", packPanel, 40, TextAnchor.MiddleLeft);
            packTitle.text = "Gói tải theo yêu cầu";
            packTitle.fontStyle = FontStyle.Bold;
            m_PackStatus = NewText("PackStatus", packPanel, 32, TextAnchor.MiddleLeft);
            var bar = NewImage("ProgressBar", packPanel, new Color(1, 1, 1, 0.12f));
            Size(bar.gameObject, 18);
            m_Progress = NewImage("Fill", bar.rectTransform, new Color(0.27f, 0.55f, 1f)).rectTransform;
            m_Progress.anchorMin = Vector2.zero;
            m_Progress.anchorMax = new Vector2(0, 1);
            m_Progress.offsetMin = m_Progress.offsetMax = Vector2.zero;
            var buttons = NewHorizontal("Buttons", packPanel, 20);
            m_DownloadButton = NewButton("Download", buttons, "Tải gói", () => StartCoroutine(DownloadAndShowPack()));
            m_ClearButton = NewButton("Clear", buttons, "Xoá cache gói", () => StartCoroutine(ClearPack()));
            m_ItemsRow = NewHorizontal("Items", packPanel, 12);
            Size(m_ItemsRow.gameObject, 130);

            m_Version = NewText("Version", column, 26, TextAnchor.MiddleCenter);
            m_Version.color = new Color(1, 1, 1, 0.5f);
        }

        void ApplyTheme(AppTheme theme)
        {
            m_Theme = theme;
            m_Background.color = theme.background;
            foreach (var panel in m_Background.GetComponentsInChildren<VerticalLayoutGroup>())
                if (panel.TryGetComponent<Image>(out var img)) img.color = theme.panel;
            foreach (var t in m_Background.GetComponentsInChildren<Text>(true)) t.color = theme.text;
            m_Progress.GetComponent<Image>().color = theme.accent;
        }

        void SetStatus(string s) => m_Status.text = s;

        void SetProgress(float p) => m_Progress.anchorMax = new Vector2(Mathf.Clamp01(p), 1);

        static string FormatBytes(long b) => b >= 1 << 20 ? (b / 1048576f).ToString("0.0") + " MB" : (b / 1024f).ToString("0") + " KB";

        Text NewText(string name, Transform parent, int size, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = m_Font;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = m_Theme != null ? m_Theme.text : Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        static Image NewImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            return img;
        }

        Button NewButton(string name, Transform parent, string label, UnityEngine.Events.UnityAction onClick)
        {
            var img = NewImage(name, parent, new Color(0.27f, 0.55f, 1f));
            var button = img.gameObject.AddComponent<Button>();
            button.onClick.AddListener(onClick);
            var layout = img.gameObject.AddComponent<LayoutElement>();
            layout.preferredHeight = 96;
            layout.flexibleWidth = 1;
            var text = NewText("Label", img.transform, 34, TextAnchor.MiddleCenter);
            text.text = label;
            Stretch(text.rectTransform);
            return button;
        }

        static void SetButtonLabel(Button b, string label) => b.GetComponentInChildren<Text>().text = label;

        static RectTransform NewVertical(string name, Transform parent, float spacing, RectOffset padding)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup));
            go.transform.SetParent(parent, false);
            var v = go.GetComponent<VerticalLayoutGroup>();
            v.spacing = spacing;
            v.padding = padding;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            return (RectTransform)go.transform;
        }

        static RectTransform NewHorizontal(string name, Transform parent, float spacing)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup));
            go.transform.SetParent(parent, false);
            var h = go.GetComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = true;
            return (RectTransform)go.transform;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        static void Size(GameObject go, float height) => go.AddComponent<LayoutElement>().preferredHeight = height;
    }
}
