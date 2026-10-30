# Changelog

## [1.2.2] - 2026-10-30

- Build & Upload: group nháp hết hạn hoặc bị xoá (404) thì hướng dẫn bấm "Đóng / build mới" thay vì để Thử lại mãi.
- Setup Addressables: cảnh báo khi đổi Content State Build Path tuỳ chỉnh về mặc định (cần chuyển `addressables_content_state.bin`).

## [1.2.1] - 2026-10-30

- Dọn file cũ chỉ khi thư mục remote tên đúng `[BuildTarget]`. Trước đây thư mục khác cũng bị dọn từ lần build thứ hai (vì manifest đã được ghi ở lần đầu), có thể xoá file không liên quan.
- Build & Upload: nút **Thử lại** khi job lỗi, tiếp tục cùng group và không build lại platform đã upload xong.
- Upload: bundle trùng tên khác MD5 trên storage dừng ngay và báo tên file, không upload lại vô ích.
- Dev local chỉ nhận đúng `localhost`, `127.0.0.1`, `[::1]` (khớp portal); bỏ `*.localhost`.

## [1.2.0] - 2026-10-30

- Hỗ trợ platform **WebGL**: manifest nhận `WebGL`, cửa sổ Build & Upload có thêm ô chọn WebGL (cần portal đã có migration 0004).

## [1.1.0] - 2026-10-20

- Thêm **Apus > Setup Addressables…**: dán JSON "Cấu hình nhanh" từ trang Settings của portal (hoặc nhập tay) → tạo/cập nhật 3 profile `dev`/`staging`/`prod` (`Remote.BuildPath`, `Remote.LoadPath`, `Apus.CatalogLoadPath`), cấu hình remote catalog, Player Version Override `content`, Build with Player, Fail Build, Content State Build Path; tuỳ chọn sửa group theo chuẩn và bật *Only update catalogs manually*. API cho script/CI: `AddressablesSetup.Apply(PortalSetupConfig, Options)`.
- Project Settings lưu thêm CDN base URL và envPath dev/staging (đầu vào của Setup Addressables).
- Manifest: tên file cho phép chữ Unicode (khớp portal mới).
- Upload: xin presigned URL theo từng đợt ngay trước khi upload đợt đó; URL cũ hơn 45 phút hoặc bị R2 trả 403 thì xin lại (tối đa 2 lần mỗi file).
- Upload: HTTP 412 (file đã có trên storage, header `If-None-Match: *`) không còn là lỗi; bước verify quyết định khớp hay sai MD5.
- Upload: một file lỗi thì huỷ các file đang upload song song; kiểm tra tên file portal trả về khớp với manifest.
- Upload: các API idempotent (tạo group theo `groupKey`, tạo release, presign, verify, chốt group) được thử lại khi lỗi mạng/5xx/408/429; không thử lại 3xx/4xx.
- Upload: URL presigned tuyệt đối được dùng nguyên văn (không ghép với Portal URL); luôn gửi `Content-Length`.
- Timeout HTTP được báo là timeout, không còn bị hiểu là người dùng huỷ. Huỷ job không dispose HttpClient khi request còn chạy.
- Build: lỗi trong *Check Addressables Config* chặn build (cửa sổ, CLI và `BuildRunner`); cảnh báo vẫn cho tiếp tục.
- Build: bundle của group remote nằm ngoài thư mục remote catalog là lỗi (trước đây bị bỏ khỏi manifest).
- Build: Build Remote Catalog tắt là lỗi khi chạy Build & Upload / CLI.
- Dọn file cũ chỉ khi thư mục tên đúng `[BuildTarget]` hoặc đã có `apus-manifest.json`, và chỉ sau khi manifest hợp lệ.
- Nếu thư mục có cả `catalog_content.json` và `.bin`, chỉ lấy file đúng định dạng catalog hiện tại.
- `gitCommit` có hậu tố `-dirty` khi working tree có thay đổi chưa commit.
- Credentials trong EditorPrefs tách theo Portal URL **và** project slug (tự đọc key cũ của 1.0.0 một lần rồi ghi sang key mới).
- CLI: `-groupKey`, `-kind`… thiếu giá trị (hoặc theo sau là flag khác) là lỗi.
- JSON parser chặt hơn (chỉ whitespace JSON, `\u` đúng 4 hex, số đúng cú pháp); `GetLong` từ chối số không nguyên.
- Log của job giữ tối đa 2000 dòng; ngày giờ định dạng InvariantCulture.

## [1.0.0]

- `apus-manifest.json` (schema 1) được ghi sau mỗi lần build Addressables có remote catalog. Các file cũ trong thư mục remote bị xoá.
- Thêm Project Settings *Apus Asset Port*; credentials lưu trong EditorPrefs hoặc lấy từ biến môi trường.
- Thêm menu *Apus > Check Addressables Config* để kiểm tra cấu hình Addressables theo mục "Cấu hình Addressables" trong README.
- Thêm *Apus > Build & Upload…*: build nhiều platform rồi upload vào một group nháp, tiếp tục được sau domain reload.
- CLI: `Apus.AssetPort.Cli.BuildAndUpload`, `Apus.AssetPort.Cli.WriteManifestOnly`.
- Hỗ trợ Addressables 1.21.x và 2.x.
