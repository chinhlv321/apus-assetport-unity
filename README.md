# Apus Asset Port – Unity

Package Unity **`com.apus.assetport`** và một **game mẫu** chạy được ngay, để đưa Addressables content (Android, iOS, WebGL) lên **Apus Asset Port**. Apus Asset Port là hệ thống host content trên Cloudflare R2, có trang quản trị web để publish và rollback content mà không phải build lại app.

| Thư mục | Nội dung |
|---|---|
| `Packages/com.apus.assetport/` | Package Editor: cấu hình Addressables một chạm, sinh manifest sau mỗi lần build, kiểm tra cấu hình, Build & Upload từ Editor hoặc CI. [README của package](Packages/com.apus.assetport/README.md) |
| `Assets/Sample/` | Game mẫu: màn hình khởi động có cập nhật catalog, content sự kiện cập nhật từ xa, và gói vật phẩm tải theo yêu cầu (có hiện dung lượng và tiến trình) |

Package **không publish**. Nó chỉ build rồi upload vào một *group nháp* trên portal. Việc đưa content tới người chơi (publish lên `dev`/`staging`/`prod`) do người có quyền làm trên trang web.

> Để upload bạn cần một portal Apus Asset Port đang chạy, một project trên portal đó và một Access Service Token cho project. Chưa có portal thì vẫn mở được project mẫu, bấm Play, build content và xem manifest; chỉ không upload được.

## Mục lục

1. [Yêu cầu](#1-yêu-cầu)
2. [Cách hệ thống hoạt động](#2-cách-hệ-thống-hoạt-động)
3. [Chạy thử game mẫu trong 5 phút](#3-chạy-thử-game-mẫu-trong-5-phút)
4. [Thử trọn vòng đời content với portal](#4-thử-trọn-vòng-đời-content-với-portal)
5. [Tích hợp vào game của bạn](#5-tích-hợp-vào-game-của-bạn)
6. [Cấu hình Addressables (chi tiết)](#6-cấu-hình-addressables-chi-tiết)
7. [Code phía client](#7-code-phía-client)
8. [Build & Upload từ Editor](#8-build--upload-từ-editor)
9. [Build & Upload trên CI](#9-build--upload-trên-ci)
10. [Hướng dẫn dùng trang web (portal)](#10-hướng-dẫn-dùng-trang-web-portal)
11. [Quy trình release](#11-quy-trình-release)
12. [Chính sách store](#12-chính-sách-store)
13. [Xử lý sự cố](#13-xử-lý-sự-cố)
14. [Phiên bản và tương thích](#14-phiên-bản-và-tương-thích)

---

## 1. Yêu cầu

- Unity **2021.3 LTS trở lên**. Project mẫu dùng **Unity 6000.0.75f1**.
- Addressables **1.21.x** hoặc **2.x** (đã thử 1.21.19 và 2.9.1). Unity 6 bắt buộc Addressables ≥ 2.9.1. Addressables 3.x/4.x **chưa được kiểm thử**.
- Module **Android Build Support** và/hoặc **iOS Build Support**. Bundle iOS build được trên Windows; chỉ bước build player qua Xcode mới cần Mac.
- Windows hoặc macOS.

## 2. Cách hệ thống hoạt động

```
Unity Editor / CI ──(Build & Upload, service token)──▶ admin.<domain>/api ──▶ R2: group nháp
Người có quyền    ──(trang web: publish / rollback)───▶ copy catalog sang live/<project>/<env>/...
Game client       ──(Addressables)────────────────────▶ cdn.<domain>  (R2 + CDN cache)
```

Cấu trúc trên CDN:

```
bundles/<project>/<platform>/<bundle>                        # bundle, tên có hash, dùng chung mọi env
live/<project>/<envPath>/<platform>/<appVersion>/catalog_*   # catalog đang active, game đọc ở đây
```

| Khái niệm | Ý nghĩa |
|---|---|
| **Group** | Một lần upload, gồm 1 release cho mỗi platform (Android, iOS). Bạn publish nguyên group để 2 platform luôn đi cùng nhau. |
| **Release** | Content của 1 platform + 1 appVersion: các bundle và 1 catalog. Kiểu `baseline` (bản app mới) hoặc `update` (đẩy content cho bản app đang chạy). |
| **Target** | `(env, platform, appVersion)`, ví dụ `prod / Android / 1.4.0`. Mỗi target trỏ tới đúng 1 release đang live. |
| **envPath** | Tên thư mục env trên CDN. `prod` giữ nguyên; `dev`/`staging` có hậu tố ngẫu nhiên theo project (ví dụ `staging-7f3k2q`) để người ngoài không đoán được path mà xem trước content chưa phát hành. |
| **appVersion** | Chính là `Application.version`. Mỗi bản app đọc catalog build cho chính nó, nên bản cũ và bản mới chạy song song được trong lúc phát hành dần. |

- **Publish** chỉ là copy catalog vào `live/`, vì bundle đã nằm sẵn trên CDN. Nhờ vậy promote từ `staging` lên `prod` không phải build lại, và rollback có hiệu lực ngay.
- Sau khi publish, client thấy catalog mới trong khoảng **60 giây** (CDN cache `catalog_content.hash` 60 giây).

---

## 3. Chạy thử game mẫu trong 5 phút

1. Clone repo, mở **thư mục gốc** bằng Unity Hub (Unity 6000.0.75f1 hoặc bản 6000.0 gần nhất). Package nằm sẵn trong `Packages/` (embedded), nên sửa code package là thấy ngay trong project mẫu.
2. Mở scene **`Assets/Sample/Scenes/Boot.unity`** (hoặc menu **Apus Sample > Open Boot Scene**) rồi bấm **Play**.

Project mẫu để Play Mode Script ở *Use Asset Database*, nên chạy được ngay mà không cần build hay mạng. Màn hình sẽ hiện:

- **Logo + theme** lấy từ content *local* (đóng sẵn trong app);
- dòng trạng thái kiểm tra content mới;
- **sự kiện** "Sự kiện mùa thu (rev 1)" kèm banner, lấy từ content *remote*, cập nhật được;
- **gói tải theo yêu cầu**: nút "Tải gói (… KB)", thanh tiến trình, 6 vật phẩm sau khi tải, và nút "Xoá cache gói" để thử lại.

### Content mẫu

| Group | Loại | Prevent Updates | Address | Dùng cho |
|---|---|---|---|---|
| `Boot` | local, đóng vào app | bật | `boot/theme`, `boot/logo` | Màn hình chờ: phải chạy được khi không có mạng |
| `Event` | remote, update được | tắt | `event/config`, `event/banner` | Live-ops: đổi mà không cần build lại app |
| `Pack1` | remote, tải theo yêu cầu | tắt | `pack1/items`, `pack1/item_01…06`, label `pack1` | DLC / level mới: chỉ tải khi người chơi cần |

### File trong game mẫu

| File | Vai trò | Copy sang game của bạn? |
|---|---|---|
| [`Scripts/ContentUpdater.cs`](Assets/Sample/Scripts/ContentUpdater.cs) | Lúc khởi động: kiểm tra và đổi sang catalog mới nhất; mất mạng thì dùng catalog đã lưu | **Có**, dùng nguyên |
| [`Scripts/ContentDownloader.cs`](Assets/Sample/Scripts/ContentDownloader.cs) | Lấy dung lượng cần tải, tải theo label có tiến trình, xoá cache | **Có**, dùng nguyên |
| [`Scripts/SampleApp.cs`](Assets/Sample/Scripts/SampleApp.cs) | Thứ tự khởi động mẫu và UI (uGUI dựng bằng code) | Tham khảo |
| `Scripts/AppTheme.cs`, `EventConfig.cs`, `ItemPack.cs` | ScriptableObject của content mẫu | Tham khảo |
| [`Editor/SampleMenu.cs`](Assets/Sample/Editor/SampleMenu.cs) | Menu **Apus Sample > Simulate Event Update** để giả lập một lần đẩy content | Không |

---

## 4. Thử trọn vòng đời content với portal

Phần này đi hết một vòng: cấu hình → upload baseline → publish staging → chạy game đọc từ CDN → đẩy content mới → rollback. Làm được với portal thật, hoặc portal chạy local (`wrangler dev`, xem README của repo server).

### 4.1 Chuẩn bị trên portal

1. Admin tạo project trên trang **Admin**, ví dụ slug `unity-spike` (slug mặc định của project mẫu; đổi slug thì đổi luôn ở bước 4.2).
2. Lead tạo **service token** và đăng ký Client ID ở trang **Settings** (xem [mục 10.8](#108-settings-project-hướng-dẫn-unity-thành-viên-service-token)).

### 4.2 Cấu hình Unity bằng một lần dán

1. Trên portal, mở **Settings** của project. Ở khung **"Thiết lập nhanh"**, bấm **Copy** để copy JSON có dạng:
   ```json
   {
     "schema": 1,
     "project": "unity-spike",
     "portalUrl": "https://admin.<domain>",
     "cdnBaseUrl": "https://cdn.<domain>",
     "envPaths": { "dev": "dev-xxxxxx", "staging": "staging-xxxxxx", "prod": "prod" }
   }
   ```
2. Trong Unity: **Apus > Setup Addressables…** → dán vào ô *Dán cấu hình từ portal* → **Đọc cấu hình** → **Apply**.
   Công cụ tạo 3 profile `dev`/`staging`/`prod`, đặt toàn bộ cấu hình ở [mục 6](#6-cấu-hình-addressables-chi-tiết), rồi chạy *Check Addressables Config*. Console phải toàn `[OK]`.
3. **Apus > Build & Upload…** → mục *Xác thực*: nhập Client ID và Client Secret.

> Project mẫu đang chứa giá trị ví dụ (`https://cdn.example.com`, `dev-abc123`, `staging-def456`) vì repo này để public. **Đừng commit domain thật hay envPath thật của bạn vào repo public.** Trong repo private của game thì commit bình thường.

### 4.3 Upload baseline và publish staging

1. **Apus > Build & Upload…**: chọn Android (và/hoặc iOS), kiểu **Baseline**, bấm *Build & Upload*. Lần đầu bắt buộc chọn Baseline, vì repo không kèm `addressables_content_state.bin`.
2. Khi xong, cửa sổ hiện link `<portal>/groups/<id>`. Mở link, bấm **Publish group → staging**, kiểm tra danh sách target rồi xác nhận.

### 4.4 Chạy game đọc content từ CDN

1. **Window > Asset Management > Addressables > Groups > Play Mode Script > Use Existing Build**.
2. Profile đang dùng phải là `staging` (Setup Addressables đặt sẵn).
3. Bấm **Play**. Game tải catalog từ `live/<project>/<staging envPath>/Android/1.0.0/`, tải bundle `event_…` và hiện "rev 1". Bấm **Tải gói** để tải bundle `pack1_…` (vài trăm KB).

### 4.5 Đẩy content mới (không build lại app)

1. **Apus Sample > Simulate Event Update**: đổi tiêu đề, màu và tăng `revision` của `EventConfig` (group remote `Event`).
2. **Apus > Build & Upload…**, kiểu **Content Update**. Log ghi "upload 1 bundle, bỏ qua 1 bundle đã có": chỉ bundle `Event` đổi, `Pack1` giữ nguyên.
3. Publish group mới lên **staging**.
4. Chạy lại game. Dòng trạng thái ghi **"Đã cập nhật content mới."** và sự kiện hiện **rev 2**; gói đã tải vẫn còn trong cache.

> Trong Editor, *Use Existing Build* dùng bản build mới nhất trong `Library/`, nên ngay sau một lần build update game có thể báo "Content đang là bản mới nhất". Trên máy thật, app cũ luôn thấy catalog mới qua CDN.

### 4.6 Rollback và chế độ offline

- Trong trang group, bấm **Rollback group** ở `staging`. Chạy lại game: game báo đã cập nhật và quay về **rev 1**.
- Tắt mạng (hoặc tắt portal local) rồi chạy game: trạng thái là **"Không kết nối được CDN, dùng content đã lưu."**, sự kiện và gói đã tải vẫn hiện từ cache.

### 4.7 Thử chặn sửa content local

Sửa `Assets/Sample/Content/Boot/AppTheme.asset` (group `Boot` bật *Prevent Updates*) rồi build **Content Update**. Build **phải bị chặn** và liệt kê asset đó: content local đã đóng vào app nên không update qua CDN được. Revert thay đổi để tiếp tục.

---

## 5. Tích hợp vào game của bạn

Làm theo thứ tự. Mỗi bước có chi tiết ở các mục sau.

1. **Cài package** vào `Packages/manifest.json`, ghim theo tag:
   ```json
   {
     "dependencies": {
       "com.apus.assetport": "https://github.com/chinhlv321/apus-assetport-unity.git?path=/Packages/com.apus.assetport#v1.1.0"
     }
   }
   ```
   Hoặc **Window > Package Manager > + > Add package from git URL…**. Trang Settings của portal hiện sẵn dòng này để copy.
   - Luôn ghim tag (`#v1.1.0`). Không ghim thì mỗi máy có thể kéo về một bản khác nhau.
   - Repo private: máy build cần quyền đọc repo, ví dụ dùng SSH `git@github.com:chinhlv321/apus-assetport-unity.git?path=/Packages/com.apus.assetport#v1.1.0`.
   - Package phụ thuộc `com.unity.addressables`. Nếu game đã có Addressables thì Unity dùng bản của game.
2. **Setup Addressables** bằng JSON từ portal ([mục 4.2](#42-cấu-hình-unity-bằng-một-lần-dán)). Bước này điền luôn Project slug và Portal URL vào **Project Settings > Apus Asset Port**. Commit `ProjectSettings/ApusAssetPortSettings.asset` và `Assets/AddressableAssetsData/`.
3. **Chia group** ([mục 6.3](#63-group)): những gì cần cho màn hình chờ và cần chạy offline → group local (*Prevent Updates*); content cập nhật được → group remote; content lớn chỉ cần khi người chơi yêu cầu → group remote riêng, có label.
4. **Copy `ContentUpdater.cs`** (và `ContentDownloader.cs` nếu có tải theo yêu cầu) vào game, gọi `ContentUpdater.Run` ở scene đầu tiên, **trước** khi load content remote ([mục 7](#7-code-phía-client)).
5. **Service token**: nhập Client ID/Secret trong *Build & Upload* (máy dev) hoặc biến môi trường (CI) ([mục 8](#8-build--upload-từ-editor), [mục 9](#9-build--upload-trên-ci)).
6. **Build player theo env**: bản QA dùng profile `staging`, bản store dùng profile `prod` ([mục 6.4](#64-build-player-theo-env)).
7. **Release đầu tiên**: Baseline → publish `staging` → test máy thật → publish **cùng group** lên `prod` → **sau đó** mới submit store ([mục 11](#11-quy-trình-release)).

---

## 6. Cấu hình Addressables (chi tiết)

**Apus > Setup Addressables…** đặt toàn bộ phần này. Phần dưới để tra cứu hoặc khi cần cấu hình tay. **Apus > Check Addressables Config** kiểm tra lại và in kèm cách sửa cho từng mục sai. Bản rút gọn của bước kiểm tra cũng tự chạy sau mỗi lần build; mục lỗi sẽ chặn Build & Upload.

### 6.1 Profile

3 profile **`dev`, `staging`, `prod`**, dùng chung cho cả Android và iOS. Các profile chỉ khác nhau ở catalog load path.

| Biến | Profile | Giá trị |
|---|---|---|
| `Remote.BuildPath` | tất cả | `ServerData/[BuildTarget]` |
| `Remote.LoadPath` (bundle) | tất cả | `https://cdn.<domain>/bundles/<project>/[BuildTarget]` |
| `Apus.CatalogLoadPath` (biến tự tạo) | mỗi env | `https://cdn.<domain>/live/<project>/<envPath>/[BuildTarget]/[UnityEngine.Application.version]` |

- **Phải dùng một biến profile riêng** cho catalog load path. Đừng chọn *Custom* ở ô Load Path của remote catalog: giá trị Custom là một chuỗi dùng chung cho mọi profile, nên không khác nhau theo env được.
- `[UnityEngine.Application.version]` được thay bằng `PlayerSettings.bundleVersion` lúc build, nên lên version không phải sửa tay.

### 6.2 Addressable Asset Settings

| Setting | Giá trị | Lý do |
|---|---|---|
| Build Remote Catalog | bật | game đọc catalog từ CDN |
| Remote catalog: Build Path / Load Path | `Remote.BuildPath` / `Apus.CatalogLoadPath` | catalog và bundle remote cùng thư mục; load path theo env |
| Player Version Override | `content` | tên catalog cố định `catalog_content.*`, portal nhận diện theo tên |
| Build Addressables on Player Build | *Build Addressables content on Player Build* | mọi máy build giống nhau |
| Check for Update Issues | **Fail Build** | chặn content update phá bản app đang chạy |
| Content State Build Path | để trống (mặc định `Assets/AddressableAssetsData/<platform>/`) | phải commit được, **không** đặt trong `Library/` |
| Only update catalogs manually | **bật** (khuyên dùng) | game tự gọi `CheckForCatalogUpdates` lúc khởi động, hiện được tiến trình và xử lý mất mạng. Tắt thì Addressables tự đổi catalog ngầm trong `InitializeAsync`, và bước kiểm tra sau đó không còn thấy gì |
| Catalog Download Timeout | ví dụ 15 giây | mạng chập chờn không làm treo màn hình chờ quá lâu |

### 6.3 Group

| Loại group | Build/Load Path | Bundle Naming | Prevent Updates |
|---|---|---|---|
| **Local** (đóng sẵn trong app) | `Local.*` | tuỳ | **bật** |
| **Remote** (tải về) | `Remote.*` | **Append Hash to Filename** | tắt |

- Group remote phải build vào **cùng thư mục** với remote catalog (`ServerData/[BuildTarget]`). Bundle nằm chỗ khác sẽ không vào manifest, và portal báo lỗi.
- Sửa asset trong group *Prevent Updates* thì content update bị chặn. Cách xử lý: chuyển asset đó sang một group remote rồi build lại.
- Content tải theo yêu cầu: gắn **label** (ví dụ `pack1`) cho mọi entry của gói, rồi dùng label đó với `GetDownloadSizeAsync` / `DownloadDependenciesAsync`.
- Tên setting có thể khác nhau đôi chút giữa các version Addressables (ví dụ *Prevent Updates* trước đây là *Cannot Change Post Release*).

### 6.4 Build player theo env

Catalog load path được ghi vào player lúc build, nên mỗi env cần một bản player riêng:

- Bản QA/test nội bộ: build với profile **`staging`**.
- Bản lên store (kể cả TestFlight, Play internal track): build với profile **`prod`**.

Content build thì **dùng chung cho mọi env** (đường dẫn bundle không chứa env); build bằng profile nào cũng được.

iOS bắt buộc HTTPS (App Transport Security). `cdn.<domain>` đã là HTTPS nên không cần ngoại lệ ATS.

---

## 7. Code phía client

### 7.1 Cập nhật catalog lúc khởi động

Gọi ở scene đầu tiên, sau khi hiện màn hình chờ (content local) và **trước** khi load content remote. Gặp lỗi mạng thì **chạy tiếp bằng catalog đã lưu**, đừng chặn người chơi.

```csharp
using Apus.Sample;   // ContentUpdater.cs copy từ game mẫu

IEnumerator Start()
{
    // 1. Màn hình chờ dùng content local: luôn có, kể cả offline.
    // 2. Kiểm tra content mới.
    yield return ContentUpdater.Run((result, error) =>
    {
        // Updated   → vừa đổi sang catalog mới
        // UpToDate  → đang là bản mới nhất
        // Offline   → không tới được CDN, dùng catalog đã lưu (error = lý do)
    });
    // 3. Từ đây load content remote như bình thường.
    var ev = Addressables.LoadAssetAsync<EventConfig>("event/config");
    yield return ev;
}
```

Bên trong, `ContentUpdater` gọi `Addressables.InitializeAsync` → `CheckForCatalogUpdates` → `UpdateCatalogs`, và release mọi handle. Cần bật *Only update catalogs manually* ([mục 6.2](#62-addressable-asset-settings)).

- Lần chạy đầu tiên trên máy mới mà không có mạng: content remote chưa từng tải nên không load được. Hiện thông báo và nút "Thử lại", như game mẫu đang làm.
- Muốn áp content mới khi game đang chạy (không chờ lần mở app sau): gọi lại `ContentUpdater.Run` ở điểm an toàn, ví dụ lúc về sảnh, và release các asset cũ trước.

### 7.2 Tải theo yêu cầu

```csharp
long bytes = 0;
yield return ContentDownloader.GetDownloadSize("pack1", (b, err) => bytes = b);  // 0 = đã có trên máy
// Hỏi người chơi: "Tải 12.3 MB?"
yield return ContentDownloader.Download("pack1", p => progressBar.value = p, err => { /* null = OK */ });
var pack = Addressables.LoadAssetAsync<ItemPack>("pack1/items");
```

Bundle được Unity cache. Từ lần thứ hai `GetDownloadSize` trả 0, không tải lại, cho tới khi có content update làm đổi bundle đó.

### 7.3 Lưu ý

- Luôn `Addressables.Release(handle)` khi không dùng nữa, nhất là trước khi `ClearDependencyCacheAsync`.
- Không đổi address hay label của asset đã phát hành mà không sửa code tương ứng trong bản app đó: bản app cũ vẫn tìm theo address cũ.
- Script (MonoBehaviour, ScriptableObject) phải có sẵn trong bản app. Content update chỉ đổi **dữ liệu**, không thêm code được ([mục 12](#12-chính-sách-store)).

---

## 8. Build & Upload từ Editor

**Apus > Build & Upload…**

1. Chọn platform (Android, iOS hoặc cả hai), tên group và ghi chú.
2. Chọn kiểu build:
   - **Content Update** (mặc định): *Update a Previous Build*, đọc `addressables_content_state.bin` trong `Assets/`. Dùng cho mọi lần đẩy content cho bản app đang chạy.
   - **Baseline**: *New Build*. **Chỉ dùng khi phát hành bản app mới.** Baseline ghi đè content state, nhớ commit file đó.
3. Bấm *Build & Upload*. Với mỗi platform, công cụ:
   - switch platform (tự chạy tiếp sau khi Unity reload script);
   - build Addressables, ghi `apus-manifest.json`;
   - upload vào **cùng một group nháp** trên portal (bundle đã có trên server được bỏ qua).
4. Xong hết, công cụ chốt group và hiện link `<portal>/groups/<id>`. Mở link để publish.

**Xác thực** (mục *Xác thực* trong cửa sổ):

- Client ID và Client Secret của **Access Service Token** do lead cấp. Mỗi token chỉ upload được vào **đúng 1 project** và không bao giờ publish được.
- Giá trị lưu **plaintext** trong `EditorPrefs` của máy (Windows: registry; macOS: plist), tách theo Portal URL và Project slug. Chúng không nằm trong project nên không bị commit, nhưng ai dùng chung tài khoản máy đều đọc được. Máy dùng chung thì dùng biến môi trường `APUS_CLIENT_ID` / `APUS_CLIENT_SECRET`.
- Portal chạy local (`http://127.0.0.1:8787`): có thể nhập *Dev email* thay cho token.

> Không bao giờ commit Client Secret, không dán vào script hay asset. Nếu lỡ lộ, báo lead để thu hồi token trên portal và trên Cloudflare Zero Trust.

**Các menu khác**

| Menu | Việc |
|---|---|
| **Apus > Setup Addressables…** | cấu hình profile + catalog bằng JSON từ portal ([mục 4.2](#42-cấu-hình-unity-bằng-một-lần-dán)) |
| **Apus > Check Addressables Config** | kiểm tra cấu hình ở mục 6 |
| **Apus > Project Settings…** | slug, Portal URL, CDN/envPath, bật/tắt ghi manifest |

Chỉ muốn build mà không upload: dùng nút build của cửa sổ Addressables Groups như bình thường. Package vẫn ghi manifest vào `ServerData/<platform>/`; sau đó kéo thả `ServerData/` vào trang **Upload** của portal ([mục 10.3](#103-upload)).

## 9. Build & Upload trên CI

```sh
export APUS_PORTAL_URL=https://admin.<domain>   # không bắt buộc, ghi đè Project Settings
export APUS_CLIENT_ID=xxxx.access
export APUS_CLIENT_SECRET=yyyy                  # lấy từ secret store của CI

# Job Android
Unity -batchmode -nographics -projectPath . -buildTarget Android -logFile - \
  -executeMethod Apus.AssetPort.Cli.BuildAndUpload \
  -groupKey "$GIT_COMMIT" -groupName "1.4.0 content" -note "CI #123" -kind update

# Job iOS, chạy sau cùng nên chốt group
Unity -batchmode -nographics -projectPath . -buildTarget iOS -logFile - \
  -executeMethod Apus.AssetPort.Cli.BuildAndUpload \
  -groupKey "$GIT_COMMIT" -complete
```

| Tham số | Ý nghĩa |
|---|---|
| `-groupKey <key>` | các job cùng key vào chung một group nháp (thường là commit hash), nên job Android và iOS chạy song song được |
| `-groupName`, `-note` | tên và ghi chú của group trên portal |
| `-kind update\|baseline` | mặc định `update` |
| `-complete` | job cuối cùng truyền cờ này để chốt group |

- **Không truyền `-quit`.** Lệnh tự thoát: mã 0 khi thành công, 1 khi lỗi.
- Project đang mở trong Unity Editor thì batchmode không mở được ("another Unity instance is running"): đóng Editor hoặc dùng bản checkout riêng cho CI.
- Windows PowerShell đặt biến môi trường bằng `$env:APUS_PORTAL_URL="https://admin.<domain>"; & "C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe" ...`.
- `Apus.AssetPort.Cli.WriteManifestOnly -kind baseline|update` chỉ build và ghi manifest, không upload.
- Cấu hình bằng script (ví dụ dựng project mới trên CI): `AddressablesSetup.Apply(new PortalSetupConfig { … }, new AddressablesSetup.Options { ActiveProfile = "prod", FixGroups = true, ManualCatalogUpdate = true })`.
- Group nháp không được chốt sau 24 giờ không hoạt động sẽ bị portal tự xoá.

---

## 10. Hướng dẫn dùng trang web (portal)

Portal ở `https://admin.<domain>`, đứng sau **Cloudflare Access**.

### 10.1 Đăng nhập, project và quyền

- Mở portal, đăng nhập bằng email công ty (Google hoặc mã gửi qua email). Header hiện email và role của bạn.
- **Chọn project** ở ô chọn trên header. Lựa chọn được nhớ, và nằm trong URL (`?project=<slug>`) nên gửi link cho người khác là mở đúng project.
- Chưa thấy project nào: nhờ lead thêm bạn vào project, hoặc nhờ admin tạo project.

| Role (theo project) | Được làm |
|---|---|
| `viewer` | xem mọi trang của project |
| `uploader` | + upload, publish và rollback `dev`/`staging`, xoá group nháp |
| `lead` | + publish và rollback **`prod`**, quản lý thành viên và service token |
| admin (toàn hệ thống) | mọi quyền trên mọi project, tạo project, quản lý user, chạy GC |
| service token (Unity/CI) | chỉ upload vào đúng 1 project; **không bao giờ** publish |

### 10.2 Dashboard

Bảng **Platform × appVersion × env** cho biết release nào đang live ở từng target (`dev`, `staging`, `prod`), group của nó, ai publish và lúc nào.

- **Rollback** một target: quay target đó về release ngay trước nó. `prod` chỉ lead/admin làm được. Hộp xác nhận đọc lại trạng thái mới nhất trước khi hiện.
- Cảnh báo **"Chưa lên prod"**: appVersion đã có ở staging nhưng chưa có ở prod. Phải publish lên prod **trước khi** submit store, nếu không người chơi tải bản app mới sẽ không có catalog.
- Cảnh báo **"Lệch group"**: Android và iOS cùng appVersion đang chạy content của 2 group khác nhau, thường do publish lẻ hoặc hotfix một bên. Nếu cố ý thì bấm **"Đã biết, bỏ qua"**.

### 10.3 Upload

Dùng khi build bằng cửa sổ Groups mà không dùng *Build & Upload* của package.

1. Kéo thả thư mục **`ServerData/`**, hoặc từng thư mục `Android/` và `iOS/`, vào khung (hoặc bấm *Chọn thư mục…*).
2. Trang đọc `apus-manifest.json` của từng platform và hiện **Xem trước**: platform, appVersion, kiểu, version Unity/Addressables, commit, số file, dung lượng, và danh sách file thừa bị bỏ qua. Platform và appVersion lấy từ manifest, không sửa được.
3. Nhập **Tên group** và **Ghi chú**, bấm *Upload*. Thanh tiến trình hiện từng file; bundle đã có trên server được bỏ qua.
4. Xong thì bấm link tới group để publish. Đóng tab giữa chừng thì group nháp tự bị xoá sau 24 giờ.

### 10.4 Groups và trang group

- **Groups**: danh sách group mới nhất, trạng thái (`draft`/`uploaded`), platform, appVersion, người tạo.
- **Trang group** (`/groups/<id>`):
  - **Publish group → dev / staging / prod**: publish mọi release trong group vào env đó trong **một deployment**. Hộp xác nhận liệt kê từng target `#cũ → #mới`. Target nào lỗi thì các target đã đổi được tự revert.
  - **Rollback group**: hoàn tác lần publish group gần nhất vào env đó. Mặc định **không** đụng các target đã bị publish đè sau group này (ví dụ một hotfix lẻ). Muốn rollback cả những target đó thì tick ô *"Rollback cả các target đã bị publish đè…"*.
  - **Publish lẻ → env**: publish riêng 1 platform, dùng cho hotfix một bên.
  - **Xem file**: danh sách bundle và catalog của release, kèm trạng thái verify.
  - **Xoá group / Xoá release**: chỉ được khi chưa từng publish.
  - **Deployments liên quan**: lịch sử publish và rollback của group.

### 10.5 Deployments

Lịch sử mọi lần publish và rollback của project. Bấm ▸ (hoặc dùng Tab + Enter) để xem từng target: release, release trước đó, trạng thái, ghi chú lỗi.

### 10.6 Files

Duyệt bucket theo project: `bundles/`, `catalog-history/`, `live/`. Có kích thước, thời điểm upload và `Cache-Control` của từng file, để kiểm tra nhanh xem file có thật trên CDN không.

### 10.7 Audit

Nhật ký mọi thao tác thay đổi dữ liệu: ai làm, lúc nào, chi tiết. Gồm cả thao tác của service token (ví dụ `token:CI build server`).

### 10.8 Settings (project): hướng dẫn Unity, thành viên, service token

- **Thiết lập nhanh**: JSON để dán vào **Apus > Setup Addressables…** ([mục 4.2](#42-cấu-hình-unity-bằng-một-lần-dán)), kèm bảng giá trị profile để cấu hình tay.
- **Hướng dẫn Unity**: dòng `manifest.json` cài package, slug và Portal URL để copy, ví dụ lệnh CI.
- **envPaths**: thư mục env của project trên CDN. Không chia sẻ ra ngoài.
- **Thành viên** (lead): thêm email và chọn role. User được tạo tự động khi được thêm vào project.
- **Service tokens** (lead):
  1. Tạo token trong **Cloudflare Zero Trust > Access > Service Auth > Service Tokens**. Lưu Client Secret ngay, vì Cloudflare chỉ hiện một lần.
  2. Đăng ký **Client ID** (dạng `xxxx.access`) và đặt tên (ví dụ `CI build server`) ở trang Settings. **Client Secret không bao giờ nhập vào portal.**
  3. Đưa Client ID và Secret cho người upload hoặc cho CI qua kênh an toàn.
  4. Khi lộ hoặc hết dùng: bấm **Thu hồi** ở portal, rồi xoá luôn token trên Cloudflare.

### 10.9 GC (admin)

Dọn bundle không còn dùng. GC chỉ xoá bundle **không** thuộc release đang live, đích rollback, baseline của appVersion còn live, 5 release được publish gần nhất của mỗi target, hay release nháp trong 7 ngày, **và** được upload hơn 30 ngày trước.

1. Chọn phạm vi (project hiện tại hoặc tất cả).
2. **Chạy thử (dry run)** để xem danh sách sẽ xoá.
3. **Xoá thật** chỉ bấm được sau khi đã chạy thử đúng phạm vi đó.

### 10.10 Admin (admin)

- **Tạo project**: slug (chữ thường, số, `-`) và tên hiển thị. envPath `dev`/`staging` được sinh ngẫu nhiên.
- **Users**: cấp hoặc thu quyền admin, xoá user. Thường không cần thêm user ở đây, vì lead thêm thành viên vào project thì user được tạo tự động.

---

## 11. Quy trình release

**Nguyên tắc:** content update **phải build từ đúng code của bản app đã phát hành**. Nếu script đã đổi (thêm MonoBehaviour, đổi field serialize…) mà bản app cũ chưa có, bundle sẽ lỗi trên máy người chơi. Vì vậy `addressables_content_state.bin` (trong `Assets/AddressableAssetsData/<platform>/`) phải được commit trong repo của game.

### Phát hành bản app mới (làm cho từng platform)

1. Tạo branch `release/<platform>/<appVersion>`.
2. Build bản QA bằng profile `staging` **trước**, rồi build bản store bằng profile `prod` **sau cùng**, cùng một commit.
3. Commit content state của **bản prod**.
4. Upload `ServerData/` của **bản prod** (kiểu *Baseline*). Đây là release baseline của appVersion đó.
5. Trên portal: publish lên `staging` → test bản QA trên máy thật → publish **cùng group** lên `prod`.
6. **Sau đó** mới submit hoặc bật bản app trên store. Nếu app lên store trước khi content lên `prod`, người chơi sẽ không tải được catalog (Dashboard có cảnh báo "Chưa lên prod").

Publish content của bản mới lên `prod` **sát ngày submit**, vì path catalog prod đoán được.

### Đẩy content cho bản app đang live

1. Checkout branch `release/...` của bản đó.
2. **Apus > Build & Upload…**, kiểu *Content Update*, chọn cả 2 platform.
3. Publish group lên `staging` → test trên máy thật → publish **cùng group** lên `prod`.

### Quy tắc khác

- **Chỉ appVersion mới nhất nhận content mới.** Bản cũ giữ content cuối cùng của nó; chỉ hotfix nghiêm trọng mới build lại cho appVersion cũ, từ branch của bản đó.
- **Mỗi bản player mới lên store phải có appVersion mới** (Android: tăng `versionName`, không chỉ `versionCode`). Ngoại lệ: appVersion chưa từng lên `prod` (ví dụ bị store reject) thì được build lại, rồi upload và publish lại baseline.
- **Nâng version Unity/Addressables** = build player mới với appVersion mới. Không làm content update qua 2 version Unity khác nhau; portal chặn việc này.
- Build iOS trên nhiều máy khác nhau có thể cho bundle khác MD5. Portal chặn bundle trùng tên khác MD5, nên tốt nhất cố định một máy (hoặc một CI runner) build iOS.

### Rollback

Rollback từng target trên **Dashboard**, hoặc cả group trong **trang group**. Client nhận lại catalog cũ trong ~60 giây. Bundle của bản trước luôn được GC giữ lại nên rollback có hiệu lực ngay.

## 12. Chính sách store

- **Apple Guideline 2.5.2**: content tải về chỉ được là **asset** (model, texture, audio, prefab, scene, ScriptableObject). Không được tải code làm thay đổi chức năng app.
- **Google Play, chính sách Device and Network Abuse**: cấm tải code thực thi từ nguồn ngoài Google Play.

Addressables mặc định đúng các quy định này vì script phải có sẵn trong bản build. **Không** kết hợp với HybridCLR, Lua hay cơ chế hot-update code nào.

## 13. Xử lý sự cố

| Triệu chứng | Nguyên nhân thường gặp |
|---|---|
| Không thấy `apus-manifest.json` | *Generate manifest* đang tắt, hoặc chưa bật *Build Remote Catalog* |
| Cảnh báo "Build không có bundle remote nào" | group remote không dùng `Remote.BuildPath`; nếu cấu hình bằng code thì phải `EditorUtility.SetDirty(schema)` cho từng schema |
| *Setup Addressables* báo envPath/URL không hợp lệ | JSON copy thiếu, hoặc sửa tay sai; copy lại từ trang Settings của portal |
| Content Update báo thiếu content state | chưa build baseline, hoặc chưa checkout đúng branch release có `addressables_content_state.bin` |
| Content Update bị chặn, liệt kê asset | asset trong group *Prevent Updates* bị sửa: revert, hoặc chuyển asset sang group remote |
| Upload báo 401/403 | sai Client ID/Secret, token chưa đăng ký trên portal, hoặc token thuộc project khác |
| Batchmode thoát ngay, log có `HandleProjectAlreadyOpenInAnotherInstance` | project đang mở trong Unity Editor: đóng Editor rồi chạy lại |
| Upload bị chặn "khác version Unity/Addressables" | appVersion này đã có release build bằng version khác; build player mới với appVersion mới |
| Upload bị chặn "bundle cùng tên khác MD5" | build không ổn định giữa các máy (thường là iOS build trên máy khác) |
| Game không nhận content mới | chưa publish đúng env của bản build; build player sai profile; chưa qua ~60 giây cache; hoặc tắt *Only update catalogs manually* mà vẫn dựa vào kết quả `CheckForCatalogUpdates` |
| Game tải catalog 404 | appVersion của app chưa có release active ở env đó (Dashboard) |
| Game báo "Không kết nối được CDN" dù có mạng | sai `cdnBaseUrl` hoặc envPath trong profile của bản build; kiểm tra bằng `curl https://cdn.<domain>/live/<project>/<envPath>/<platform>/<appVersion>/catalog_content.hash` |

## 14. Phiên bản và tương thích

- Package theo semver; mỗi bản phát hành có tag `vX.Y.Z` trên repo này. Xem [CHANGELOG](Packages/com.apus.assetport/CHANGELOG.md).
- Package **1.x** dùng API portal **v1** và manifest **schema 1**. Bản package đổi major khi cần portal mới hơn.
- 1.1.0 cần portal có khung "Thiết lập nhanh" ở trang Settings để copy JSON; với portal cũ thì nhập tay các ô trong *Setup Addressables*.

### Phát hành bản package mới (cho người bảo trì)

1. Sửa code trong `Packages/com.apus.assetport/`, thử với game mẫu (mục 3 và 4).
2. Tăng `version` trong `Packages/com.apus.assetport/package.json`, ghi `CHANGELOG.md`.
3. Commit, tag và push:
   ```sh
   git tag v1.1.0
   git push origin master --tags
   ```
4. Các game đổi tag trong `manifest.json` để nâng.
