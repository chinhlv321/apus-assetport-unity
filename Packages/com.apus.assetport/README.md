# Apus Asset Port (`com.apus.assetport`)

Package Editor cho Unity 2021.3+ với Addressables 1.21.x hoặc 2.x. Package làm bốn việc:

- Cấu hình Addressables theo chuẩn của portal chỉ bằng một lần dán (**Apus > Setup Addressables…**).
- Sau mỗi lần build Addressables, ghi `apus-manifest.json` vào thư mục remote (`ServerData/<platform>/`). Áp dụng cho cả build từ cửa sổ Groups lẫn build cùng player. Đồng thời xoá các file cũ trong thư mục đó mà build này không sinh ra.
- Kiểm tra cấu hình Addressables theo chuẩn của Apus Asset Port.
- Build rồi upload content lên portal: dùng menu trong Editor, hoặc chạy batchmode trên CI. Package **không bao giờ publish**.

Hướng dẫn đầy đủ (cấu hình Addressables, code client, quy trình release, xử lý sự cố) nằm ở [README của repo](../../README.md).

## Cài đặt

Thêm vào `Packages/manifest.json`:

```json
"com.apus.assetport": "https://github.com/chinhlv321/apus-assetport-unity.git?path=/Packages/com.apus.assetport#v1.2.0"
```

Nếu repo private, máy build cần quyền đọc repo (ví dụ SSH: `git@github.com:chinhlv321/apus-assetport-unity.git?path=/Packages/com.apus.assetport#v1.2.0`).

## Thiết lập

**Project Settings > Apus Asset Port.** Các giá trị lưu ở `ProjectSettings/ApusAssetPortSettings.asset`; hãy commit file này.

| Trường | Ý nghĩa |
|---|---|
| Project slug | slug của project trên portal, ví dụ `skyrise` |
| Portal URL | `https://admin.<domain>`, hoặc `http://127.0.0.1:8787` khi portal chạy local |
| Generate manifest | ghi manifest sau mỗi lần build (mặc định bật) |

**Xác thực bằng Access service token.** Client ID và Secret nhập trong cửa sổ *Build & Upload*. Chúng được lưu trong `EditorPrefs` của máy, tách theo Portal URL và Project slug, và **không** nằm trong project. Trên CI dùng `APUS_CLIENT_ID` / `APUS_CLIENT_SECRET`.

> `EditorPrefs` lưu **plaintext** (Windows: registry `HKCU\Software\Unity Technologies\Unity Editor 5.x`; macOS: `~/Library/Preferences/com.unity3d.UnityEditor5.x.plist`). Ai dùng chung tài khoản máy đều đọc được Secret. Máy dùng chung thì nhập token qua biến môi trường thay vì lưu trong Editor.

Khi portal chạy ở localhost, có thể nhập *Dev email* thay cho token. Package sẽ gửi header `X-Dev-Email`. Nếu chỉ có Client ID, package gửi thêm `X-Dev-Service-Token`.

**Cấu hình Addressables: Apus > Setup Addressables…**

1. Trên portal, mở trang **Settings** của project, bấm Copy ở khung *Thiết lập nhanh* để lấy JSON `{"schema":1,"project":…,"portalUrl":…,"cdnBaseUrl":…,"envPaths":{…}}`.
2. Dán vào cửa sổ, bấm *Đọc cấu hình* rồi *Apply*. Công cụ sẽ:
   - tạo hoặc cập nhật 3 profile `dev`/`staging`/`prod` với `Remote.BuildPath` = `ServerData/[BuildTarget]`, `Remote.LoadPath` = `<cdn>/bundles/<project>/[BuildTarget]` và `Apus.CatalogLoadPath` = `<cdn>/live/<project>/<envPath>/[BuildTarget]/[UnityEngine.Application.version]`;
   - bật Build Remote Catalog, đặt Build Path = `Remote.BuildPath` và Load Path = `Apus.CatalogLoadPath` (không dùng *Custom*, vì giá trị Custom dùng chung cho mọi profile);
   - đặt Player Version Override = `content`, Build Addressables on Player Build, Check for Update Issues = Fail Build, Content State Build Path mặc định;
   - tuỳ chọn: sửa group theo chuẩn (remote: Append Hash + `Remote.*`; local: Prevent Updates), và bật *Only update catalogs manually* (khuyên dùng, xem [README mục 7](../../README.md#7-code-phía-client));
   - điền Project slug, Portal URL, CDN và envPath vào Project Settings, rồi chạy *Check Addressables Config*.

Chạy lại bao nhiêu lần cũng được: công cụ chỉ ghi đè các giá trị nó quản lý. Gọi bằng script hoặc CI: `AddressablesSetup.Apply(new PortalSetupConfig { Project, PortalUrl, CdnBaseUrl, EnvPathDev, EnvPathStaging }, new AddressablesSetup.Options { ActiveProfile = "staging", FixGroups = true, ManualCatalogUpdate = true })`.

Chi tiết từng setting và lý do: [README mục 6](../../README.md#6-cấu-hình-addressables-chi-tiết).

## Menu

- **Apus > Setup Addressables…**: cấu hình profile và catalog bằng JSON từ portal (xem trên).
- **Apus > Build & Upload…**
  - Chọn Android/iOS/WebGL, tên group, ghi chú và kiểu build:
    - *Content Update* (mặc định): Update a Previous Build, đọc `addressables_content_state.bin` trong `Assets/`.
    - *Baseline*: New Build. Chỉ dùng khi phát hành bản app mới.
  - Với mỗi platform, công cụ switch platform (tự chạy tiếp sau khi Unity reload), build, rồi upload vào **cùng một group nháp**.
  - Cuối cùng công cụ chốt group và đưa link `<portal>/groups/<id>`. Việc publish làm trên portal.
- **Apus > Check Addressables Config**: in ra từng mục kiểm tra kèm cách sửa. Bản rút gọn tự chạy sau mỗi lần build.
- **Apus > Project Settings…**

## CI (batchmode)

```sh
export APUS_PORTAL_URL=https://admin.example.com   # không bắt buộc, ghi đè Project Settings
export APUS_CLIENT_ID=xxxx.access
export APUS_CLIENT_SECRET=yyyy
Unity -batchmode -nographics -projectPath . -buildTarget Android -logFile - \
  -executeMethod Apus.AssetPort.Cli.BuildAndUpload \
  -groupKey "$GIT_COMMIT" -groupName "1.4.0 content" -note "CI #123" -kind update
Unity -batchmode -nographics -projectPath . -buildTarget iOS -logFile - \
  -executeMethod Apus.AssetPort.Cli.BuildAndUpload -groupKey "$GIT_COMMIT" -complete
```

- Không truyền `-quit`. Lệnh tự thoát với mã 0 khi thành công, 1 khi lỗi.
- Các job cùng `-groupKey` đi vào chung một group nháp. Job cuối truyền `-complete` để chốt group.
- `-kind`: `update` (mặc định) hoặc `baseline`.
- `Apus.AssetPort.Cli.WriteManifestOnly -kind baseline|update` chỉ build và ghi manifest, không upload.
- Flag cần giá trị mà thiếu giá trị (ví dụ `-kind -complete`) là lỗi. Giá trị không được bắt đầu bằng `-` + chữ cái.
- Lỗi trong *Check Addressables Config* làm build thất bại (mã thoát 1); cảnh báo chỉ in ra log.

Bundle iOS build được trên Windows nếu đã cài module iOS Build Support.

## Manifest (`apus-manifest.json`, schema 1)

Portal dùng file này để biết platform, appVersion và danh sách file cần upload, nên **không sửa tay**.

| Trường | Ý nghĩa |
|---|---|
| `schema` | `1` |
| `project` | slug trong Project Settings |
| `platform` | `Android` / `iOS` / `WebGL` (đúng tên `[BuildTarget]`) |
| `appVersion` | `PlayerSettings.bundleVersion` |
| `kind` | `baseline` (New Build / build cùng player) hoặc `update` |
| `unityVersion`, `addressablesVersion` | version lúc build; portal chặn content update khác version với baseline |
| `gitCommit`, `builtAt` | commit hiện tại (null nếu không có git) và thời điểm build |
| `files[]` | `name`, `size`, `md5` của từng bundle và file catalog |

File trong `ServerData/` mà không có tên trong manifest sẽ không được upload.

`gitCommit` có hậu tố `-dirty` khi file đã track trong git có thay đổi chưa commit (`git status --porcelain --untracked-files=no`).

## Dọn file cũ

Sau khi manifest hợp lệ, package xoá các file trong thư mục remote mà build này không sinh ra. Package chỉ dọn khi thư mục tên đúng `[BuildTarget]` (`Android`/`iOS`/`WebGL`) hoặc đã có `apus-manifest.json` từ lần build trước, và không bao giờ dọn thư mục gốc project, `Assets/`, `Library/`, `Packages/`… Trường hợp khác chỉ in cảnh báo.

## Upload

- Presigned URL được xin theo từng đợt (≤500 file) ngay trước khi upload đợt đó. URL cũ hơn 45 phút, hoặc bị R2 trả 403, được xin lại.
- Header từ presign có `If-None-Match: *`: file đã có trên storage thì R2 trả 412, package coi là "đã có" và để bước verify so MD5.
- 4 file upload song song; một file lỗi (sau 3 lần thử lại) thì huỷ các file còn lại.

**Bộ nhớ khi upload (Unity 2021/2022, Mono).** Package upload bằng `HttpClient` và `StreamContent` đọc thẳng từ file, luôn đặt `Content-Length`. Trên một số bản Mono, `HttpClientHandler` (dựa trên `HttpWebRequest`) vẫn có thể đệm cả body vào RAM trước khi gửi, nên mỗi file đang upload có thể chiếm RAM bằng kích thước file (tối đa 4 file cùng lúc). Bundle rất lớn (hàng trăm MB) có thể làm Editor tốn vài GB RAM; tách bundle nhỏ hơn (Bundle Mode = Pack Separately / Pack Together By Label) nếu gặp. Package cảnh báo bundle > 200 MB khi ghi manifest.
