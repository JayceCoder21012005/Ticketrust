# TicketRush

Nền tảng bán vé flash-sale: hàng chục nghìn người bấm "Mua" cùng một giây, hệ thống vẫn phải công bằng,
nhanh và **không bao giờ bán trùng một ghế**. Toàn bộ đặc tả nằm trong [specs/](specs/README.md).

## Mục lục

- [Tiến độ](#tiến-độ)
- [Yêu cầu](#yêu-cầu)
- [Khởi chạy](#khởi-chạy)
- [Tài khoản dùng để test](#tài-khoản-dùng-để-test)
- [Những gì có thể thử](#những-gì-có-thể-thử)
- [Chạy frontend riêng](#chạy-frontend-riêng)
- [Kiểm thử](#kiểm-thử)
- [Xử lý sự cố](#xử-lý-sự-cố)
- [Cấu trúc thư mục](#cấu-trúc-thư-mục)
- [Triển khai production](#triển-khai-production)

## Tiến độ

| Spec | Nội dung | Trạng thái |
|---|---|---|
| [01](specs/01-project-setup-and-infrastructure.md) | Khung dự án, Aspire, outbox, CI | ✅ Hoàn thành |
| [02](specs/02-frontend-design-system.md) | Design system, component dùng chung, budget hiệu năng | ✅ Hoàn thành |
| [03](specs/03-auth-and-user-accounts.md) | Đăng ký, đăng nhập, refresh token, hồ sơ, phân quyền | ✅ Hoàn thành |
| [04](specs/04-event-catalog-and-search.md) | Trang chủ, danh sách và tìm kiếm sự kiện, trang chi tiết, cache | ✅ Hoàn thành |
| [05](specs/05-organizer-event-management.md) | Khu nhà tổ chức: địa điểm, wizard tạo sự kiện, sơ đồ ghế, đợt mở bán, xuất bản, huỷ | ✅ Hoàn thành |
| [06](specs/06-virtual-waiting-room.md) | Phòng chờ: hàng đợi ngẫu nhiên trước giờ mở bán, nhận người theo tốc độ, admission token gắn với tài khoản, trạng thái phòng theo kho vé. Còn CAPTCHA (12), waitlist (13) | 🟡 Gần xong |
| [07](specs/07-seat-selection-and-hold.md) | Chọn ghế trên sơ đồ trực tiếp, giữ ghế 8 phút bằng Lua nguyên tử trên Redis, cập nhật thời gian thực qua SignalR | ✅ Hoàn thành |
| [08](specs/08-checkout-and-payment.md) | Thanh toán: đơn hàng từ lượt giữ ghế, cổng thanh toán giả lập (ký webhook, chống gửi trùng, thanh toán trễ), phát vé đúng một lần, hoàn tiền tự động, các trang checkout và `/account/orders`. Đã chạy k6 `load-tests/sale.js` trên stack compose (50 người mua đồng thời, 0 vi phạm bất biến) | ✅ Hoàn thành |
| [09](specs/09-notifications-and-email.md) | Thông báo và email: chuông thông báo, trang `/account/notifications`, toast thời gian thực qua SignalR, email giao qua bảng `email_deliveries` và worker (thử lại, dead-letter), 8 mẫu email tiếng Việt kiểm tra bằng Mailpit, nút "Nhắc tôi" và nhắc lịch (mở bán trước 30 phút, sự kiện trước 24 giờ). Xem [runbook email](docs/runbooks/email-delivery.md). Còn lại cho spec 13 và 14: thông báo waitlist và chuyển vé | ✅ Hoàn thành |
| [10](specs/10-e-tickets-and-rotating-qr.md) | Vé điện tử: trang `/account/tickets`, ví vé `/tickets/[id]` với mã QR đổi mỗi 15 giây (tính phía trình duyệt bằng Web Crypto, bộ mã hoá QR viết trong repo, kiểm tra bằng zxing-cpp 1049/1049), `GET /me/tickets`, `/tickets/{id}`, `/tickets/{id}/qr-key` (chỉ chủ vé, giới hạn 10 lần/phút), dùng được khi mất mạng nhờ kho khoá IndexedDB và service worker (chỉ vé đã mở khi có mạng trên thiết bị đó). Xem [runbook khoá QR](docs/runbooks/ticket-qr-keys.md). Còn lại cho spec 11 (quét tại cổng, giờ check-in), 12 (giới hạn tốc độ `qr-key` thay bản Redis tạm) và 14 (đổi bí mật khi chuyển vé) | ✅ Hoàn thành |
| [11](specs/11-gate-check-in.md) – [17](specs/17-load-testing-and-observability.md) | Quét vé tại cổng… (spec 17 mới có kịch bản bán vé và bộ kiểm tra bất biến trong `load-tests/`) | ⏳ Chưa làm |

## Yêu cầu

| Công cụ | Phiên bản |
|---|---|
| .NET SDK | 10.0.403 trở lên (xem `global.json`) |
| Node.js | 24 trở lên |
| Docker Desktop | Đang chạy (Postgres, Redis và Mailpit chạy trong container) |
| Aspire CLI | Tuỳ chọn: `dotnet tool install -g Aspire.Cli` để dùng `aspire run` |

## Khởi chạy

```bash
# 1. Cài thư viện cho web (lần đầu, hoặc khi package.json thay đổi)
cd web && npm ci && cd ..

# 2. Tin cậy chứng chỉ HTTPS dev (chỉ cần một lần trên mỗi máy)
dotnet dev-certs https --trust

# 3. Chạy toàn bộ hệ thống
dotnet run --project src/TicketRush.AppHost      # hoặc: aspire run
```

1. Terminal in ra link **Aspire Dashboard** có kèm token đăng nhập, dạng `https://localhost:17180/login?t=…`. Mở link đó.
2. Đợi các resource chuyển sang **Running**. Riêng `migrations` dừng ở **Finished**, vì đó là job chạy một lần.
3. Bấm vào URL của resource **web** để mở ứng dụng. Port do Aspire cấp nên mỗi máy có thể khác.

Các resource Aspire khởi động:

| Resource | Vai trò |
|---|---|
| `postgres` (+ pgAdmin) | Cơ sở dữ liệu chính, database `ticketrush` |
| `redis` (+ RedisInsight) | Cache, khoá phân tán, khoá Data Protection |
| `mailpit` | Hộp thư giả: mọi email của hệ thống đều nằm ở đây, không gửi ra ngoài |
| `migrations` | Chạy migration và nạp dữ liệu mẫu, rồi dừng |
| `api` | ASP.NET Core Minimal API (`/api/v1/*`) |
| `worker` | Background service: gửi outbox và email, hết hạn đơn hàng, hoàn tiền, giao webhook của cổng thanh toán giả lập |
| `web-installer` | Chạy `npm install` cho `web/` trước khi `web` khởi động |
| `web` | Next.js (`npm run dev`); chuyển tiếp `/api`, `/hubs`, `/media` sang `api` |

Dữ liệu Postgres và Redis được giữ trong named volume giữa các lần chạy. Dừng hệ thống bằng `Ctrl+C`.

Lần chạy đầu, AppHost tự sinh các secret dev và lưu vào user-secrets của AppHost, không bao giờ vào git:

| Tham số | Dùng cho |
|---|---|
| `jwt-signing-key` | Ký access token (spec 03) |
| `access-code-pepper` | Hash mã presale: `SHA-256(pepper ‖ mã)` (spec 05) |
| `admission-token-signing-key` | Ký admission token của phòng chờ, khác khoá của access token (spec 06) |
| `revalidate-secret` | Worker gọi `POST /api/revalidate` của web để làm mới trang công khai sau khi xuất bản, sửa hoặc huỷ sự kiện (spec 05) |

## Tài khoản dùng để test

> ⚠️ Chỉ có trong môi trường dev: dữ liệu mẫu chỉ được nạp khi `Migrations__SeedDevData=true` (AppHost bật sẵn).
> Không bao giờ dùng các tài khoản này ở production.

Tất cả dùng chung mật khẩu **`Dev@12345`** và đều đã xác nhận email.

| Email | Họ tên | Vai trò | Dùng để thử |
|---|---|---|---|
| `customer@ticketrush.dev` | Nguyễn Văn Khách | `Customer` | Người mua vé: hồ sơ, mua vé, vé của tôi |
| `organizer@ticketrush.dev` | Trần Thị Tổ Chức | `Organizer` | Nhà tổ chức: quản lý sự kiện, dashboard bán vé |
| `admin@ticketrush.dev` | Lê Quản Trị | `Admin` | Quản trị: hoàn tiền, audit log |
| `gate@ticketrush.dev` | Phạm Soát Vé | `GateStaff` | Nhân viên soát vé tại cổng |

Khu nhà tổ chức (`/organizer`) có từ spec 05; khu Admin và GateStaff sẽ có từ spec 16 và 11. Các policy phân
quyền đã có sẵn ở API từ spec 03.

**Tạo tài khoản mới:** đăng ký tại `/register`. Mật khẩu cần ít nhất 10 ký tự, gồm cả chữ và số, và không
trùng với email. Email xác nhận nằm trong **Mailpit** (mở từ dashboard); bấm link trong email để xác nhận.
Tài khoản chưa xác nhận vẫn đăng nhập được, nhưng sẽ không vào được phòng chờ và không mua được vé.

**Dữ liệu mẫu khác:**
- 3 địa điểm **dùng chung**: Nhà thi đấu Phú Thọ (TP.HCM), Sân vận động Mỹ Đình (Hà Nội), Cung thể thao Tiên Sơn
  (Đà Nẵng). Mọi nhà tổ chức đều chọn được nhưng không sửa được; địa điểm tạo thêm chỉ người tạo thấy.
- Đợt "Presale" của "Đen Vâu - Show Của Đen" dùng mã **`seed-presale`** (không phân biệt hoa thường).
- 15 sự kiện thuộc đủ 6 danh mục và mọi trạng thái: 12 đang hiển thị, 1 bản nháp, 1 đã kết thúc, 1 đã huỷ.
- "Sơn Tùng M-TP Live" và "Hà Anh Tuấn - Chân Trời Rực Rỡ" có sơ đồ 600 ghế (3 khu × 10 hàng × 20 ghế).
- Các mốc mở bán tính từ **lần seed đầu tiên**:

  | Sự kiện | Mở bán | Dùng để thử |
  |---|---|---|
  | Sơn Tùng M-TP Live, Mỹ Tâm, V.League… | Đang mở | Nút "Mua vé" |
  | Hà Anh Tuấn - Chân Trời Rực Rỡ | Sau 5 phút | Phòng chờ (spec 06) |
  | Vũ Cát Tường - Inner Space | Sau 20 phút | Nút đổi từ đếm ngược sang "Vào phòng chờ" lúc còn 30 phút |
  | Đen Vâu, HOZO, Marathon Đà Nẵng… | Sau vài ngày | Đếm ngược, badge "Mở bán …" |

  Seed chỉ thêm những gì chưa có. Muốn các mốc trên tính lại từ bây giờ, xoá volume Postgres (xem [Xử lý sự cố](#xử-lý-sự-cố)).

## Những gì có thể thử

| Đường dẫn | Nội dung |
|---|---|
| `/` | Trang chủ: hero với ô tìm kiếm (ảnh nền từ màn hình tablet trở lên), Sắp mở bán (đếm ngược), Đang hot, Diễn ra tuần này, ô thành phố và danh mục có số sự kiện, Sự kiện sắp diễn ra, "Dành cho bạn" khi đã đăng nhập và có theo dõi sự kiện. Mỗi sự kiện chỉ xuất hiện ở một mục |
| `Ctrl K` / `⌘K` (mọi trang) | Bảng tìm kiếm tức thì: gõ không dấu, gõ tắt ("son tu"), sai chính tả nhẹ; kết quả chia nhóm Sự kiện, Nghệ sĩ, Địa điểm, Thành phố; mũi tên + Enter để mở; nhớ 5 lần tìm gần nhất. Ô trống hiện các sự kiện đang hot |
| `/events` | Danh sách sự kiện: tìm không dấu ("son tung"), lọc theo danh mục, thành phố, ngày, giá; "Xem thêm" |
| `/events/son-tung-mtp-live` | Trang chi tiết: nút mua vé theo trạng thái mở bán, "Thêm vào lịch" (.ics), bản đồ, bảng giá, FAQ |
| `/login`, `/register` | Đăng nhập, đăng ký (hiện/ẩn mật khẩu, thanh độ mạnh mật khẩu) |
| `/forgot-password`, `/reset-password` | Quên và đặt lại mật khẩu. Email nằm trong Mailpit, link có hiệu lực 1 giờ |
| `/verify-email` | Trang mở từ link xác nhận email |
| `/account` | Khu tài khoản: Tổng quan (sự kiện sắp tới của bạn và nút mở vé, đơn gần đây, sự kiện đang theo dõi), menu sang Vé, Đơn hàng, Thông báo, Hồ sơ |
| `/account/profile` | Hồ sơ, số điện thoại, ảnh đại diện (JPEG/PNG/WebP/AVIF, tối đa 2 MB, tự cắt vuông 512 px trước khi tải lên, hiện trên header), đăng xuất |
| `/events/{slug}/seats` | Chọn ghế (cần lượt vào từ phòng chờ). Bấm "Mua vé" ở trang sự kiện → phòng chờ → tự chuyển sang trang này. Chọn ghế trên sơ đồ (chuột hoặc phím mũi tên + Enter), hoặc "Chọn chỗ tốt nhất", rồi "Tiếp tục" để giữ ghế 8 phút. Ghế người khác vừa giữ đổi trạng thái ngay, không cần tải lại |
| `/events/{slug}/queue` | Phòng chờ (cần đăng nhập và email đã xác nhận). Sự kiện mẫu "Hà Anh Tuấn" mở bán sau 5 phút, "Vũ Cát Tường" sau 20 phút: vào phòng chờ trước giờ mở để được xếp ngẫu nhiên, khi mở bán sẽ được nhận dần 50 người/giây (cấu hình `Queue` trong `appsettings.json`) |
| `/events/{slug}/checkout` | Bước người mua (sau khi giữ ghế): tên, email, số điện thoại, đếm ngược giữ chỗ. Bấm "Tiếp tục" tạo đơn hàng rồi chuyển sang `/checkout/{orderId}` |
| `/checkout/{orderId}` | Tóm tắt đơn, điều khoản, "Thanh toán" (bấm đúp vẫn chỉ tạo một phiên), "Huỷ đơn" khi đơn còn `Pending`. Chuyển sang cổng thanh toán giả lập `/mock-gateway/{sessionId}`: *Thành công*, *Thất bại*, *Thành công – webhook trễ 3 phút*, *Gửi webhook 2 lần*. Trang chỉ có khi cổng giả lập bật (mặc định bật ở dev, tắt ở production: trả 404) |
| `/checkout/{orderId}/result` | Kết quả: đang xác nhận, thành công, thất bại (thử lại), hết hạn, đang hoàn tiền (kèm lý do), đã hoàn tiền |
| `/account/orders` | Đơn hàng của tôi (danh sách, "Xem thêm") và chi tiết có dòng thời gian trạng thái |
| `/design` | Design system: token màu, component dùng chung, màu ghế trên sơ đồ |
| `/organizer/events` | Đăng nhập bằng `organizer@ticketrush.dev`. Danh sách sự kiện (lọc theo trạng thái, tìm không dấu), nút "Tạo sự kiện" (5 trường, tạo ngay bản nháp) |
| `/organizer/events/{id}/info` → `review` | Wizard 5 bước, tự lưu: thông tin và ảnh cover cắt 16:9 → hạng vé → sơ đồ (mẫu dựng sẵn, xem trước SVG) → đợt mở bán (dòng thời gian, mã presale) → checklist, xem trước trang người mua, xuất bản, huỷ |
| `/organizer/venues` | Địa điểm dùng chung và địa điểm riêng, "Thêm địa điểm" |

## Chạy frontend riêng

Aspire tự chạy `npm run dev` cho `web`, và log nằm ở **Console logs** của resource `web` trên dashboard.
Nếu muốn chạy Next.js trong terminal riêng:

1. Vẫn chạy AppHost để có `api`, `worker`, Postgres, Redis và Mailpit.
2. Bấm **Stop** resource `web` trên dashboard. Hai Next dev server không được chạy chung một thư mục `web/`.
3. Chạy:

   ```bash
   cd web
   npm run dev                                         # http://localhost:3000
   # api ở port khác 5080? → API_URL=http://localhost:<port> npm run dev
   ```

Link trong email xác nhận và đặt lại mật khẩu vẫn trỏ về URL `web` của Aspire, vì API lấy giá trị
`Frontend:BaseUrl` từ Aspire.

## Kiểm thử

```bash
# Backend (integration test cần Docker, dùng Testcontainers)
dotnet test --project tests/TicketRush.Domain.Tests
dotnet test --project tests/TicketRush.Application.Tests
dotnet test --project tests/TicketRush.ArchitectureTests
dotnet test --project tests/TicketRush.Api.IntegrationTests

# Bất biến bán vé (chỉ cần Docker, vì dùng Testcontainers): cùng truy vấn SQL với lần chạy k6 bên dưới
dotnet test --project tests/TicketRush.Api.IntegrationTests --filter-class "*SaleInvariantTests"

# Frontend
cd web
npm run lint && npm run typecheck && npm test
ENABLE_DESIGN_PAGE=1 npm run build:e2e   # build kèm mock api (e2e/mock-api.mjs), giống CI
npx playwright install chromium     # lần đầu
npm run test:e2e                    # axe, font tiếng Việt, INP, đăng nhập, catalog (dùng mock api)
npm run lighthouse                  # budget LCP/TBT/CLS/JS (cần Chrome)
```

### Load test bán vé và kiểm tra bất biến

`load-tests/sale.js` (k6) cho nhiều người mua cùng lúc đi hết luồng admission, giữ ghế, tạo đơn, thanh toán và nhận webhook
(thành công, thất bại, trễ, gửi 2 lần, bỏ dở), có chủ ý tranh nhau ghế nóng và đòi vé GA nhiều hơn sức chứa. Sau đó
`load-tests/check-sale-invariants.sh` chạy `load-tests/sql/sale-invariants.sql` và thoát với mã khác 0 nếu có vi phạm:
một ghế có hơn một vé chưa bị huỷ, `sold_count` vượt sức chứa hoặc lệch số vé GA, đơn `Paid` thiếu vé, đơn chưa trả tiền hoặc
đang hoàn tiền mà còn vé. Hướng dẫn đầy đủ (stack compose, dữ liệu mẫu, biến môi trường) nằm ở [load-tests/README.md](load-tests/README.md).

> ⚠️ Luôn thêm `-p ticketrush-loadtest` vào mọi lệnh `docker compose` của load test: tên project khác làm volume và container tách hẳn khỏi stack `ticketrush` thật (volume thành `ticketrush-loadtest_postgres-data`, ...), nên không thể dùng lại hay ghi đè dữ liệu thật. Không bao giờ chạy overlay này trên máy chủ production; script k6 và script kiểm tra từ chối host/CSDL tự chọn nếu thiếu `LOADTEST_CONFIRM=loadtest`.

```bash
docker compose -p ticketrush-loadtest -f docker-compose.prod.yml -f load-tests/docker-compose.loadtest.yml --env-file .env up -d --build
k6 run -e EVENT_ID=<uuid> -e GA_TIER_ID=<uuid> -e BUYERS=500 -e VUS=100 load-tests/sale.js
ENV_FILE=.env load-tests/check-sale-invariants.sh      # exit 0 = mọi bất biến đúng (mặc định nhắm vào project ticketrush-loadtest)
```

Bản tự động của cùng truy vấn là `SaleInvariantTests` (dòng `dotnet test` ở trên). Lần chạy k6 thật (2026-11-10, laptop, một node, `BUYERS=500 VUS=50`, GA thu nhỏ còn 60): 253 đơn `Paid`, 64 `Expired`, 2 `Refunded`, mỗi ghế tối đa 1 vé, `sold_count` GA 52/60, `sale_unexpected_responses` = 0, 
`check-sale-invariants.sh` thoát với mã 0 (ngay sau khi chạy và 3 phút sau, khi webhook trễ đã về). p99 giữ ghế 244 ms và checkout 764 ms nên chỉ qua với ngưỡng cho laptop (`HOLD_P99_MS=1000 CHECKOUT_P99_MS=2000`); mức mặc định 150/300 ms là ngân sách spec 17 cho máy riêng. 
Với `VUS=100` bất biến vẫn đúng nhưng 6 lần đăng nhập trả 500 vì Postgres hết kết nối (`max_connections` 100): giữ `VUS` ở 50 với cấu hình mặc định. Chi tiết và ghi chú chạy ở [load-tests/README.md](load-tests/README.md).

CI (`.github/workflows/ci.yml`) gồm tất cả các bước trên (trừ lần chạy k6). Nó cũng kiểm tra API client không bị lệch so với
OpenAPI, và audit dependency: code ship ra production không được có lỗ hổng nào, công cụ dev không được có
lỗ hổng high/critical.

### Chạy CI ở máy trước khi push

CI trên GitHub đang **tạm dừng** (gói miễn phí chỉ có 2.000 phút Actions mỗi tháng): workflow chỉ chạy khi bấm tay ở tab
Actions (`workflow_dispatch`). Trước mỗi lần push, chạy cùng các bước đó ở máy bằng `scripts/ci-local.ps1`. Script chạy
đúng thứ tự như CI và dừng ở bước lỗi đầu tiên:

```powershell
powershell -File scripts/ci-local.ps1                  # đầy đủ như CI
powershell -File scripts/ci-local.ps1 -Only backend    # chỉ backend
powershell -File scripts/ci-local.ps1 -Only web        # chỉ web
powershell -File scripts/ci-local.ps1 -SkipLighthouse  # bỏ Lighthouse, bước chậm nhất
```

Test tích hợp cần Docker Desktop đang chạy. Có thêm `-SkipIntegration` và `-SkipE2E` khi chỉ muốn kiểm tra nhanh. Để bật lại
CI trên GitHub, khôi phục trigger `pull_request` và `push` ở đầu `ci.yml` (comment trong file ghi sẵn).

## Xử lý sự cố

| Triệu chứng | Cách xử lý |
|---|---|
| `web-installer` lỗi với exit code `-4051` (`ENOTEMPTY`) | Có tiến trình khác đang giữ file trong `web/node_modules` (một dev server khác, Lighthouse hoặc test đang chạy). Tắt các tiến trình đó rồi **Restart** `web-installer`. |
| `web` thoát ngay với lỗi `Cannot find module …` | `node_modules` bị cài dở. Dừng AppHost, chạy `cd web && rm -rf node_modules && npm ci`, rồi chạy lại. |
| Trình duyệt báo lỗi chứng chỉ HTTPS | Chạy `dotnet dev-certs https --trust`. |
| Muốn làm lại dữ liệu từ đầu | Dừng AppHost, chạy `docker volume rm ticketrush-postgres-data ticketrush-redis-data`. |
| Mã `seed-presale` không dùng được trên database tạo trước spec 05 | Database cũ lưu mã chưa hash. Xoá volume Postgres như dòng trên để seed lại. |

## Cấu trúc thư mục

| Đường dẫn | Nội dung |
|---|---|
| `src/TicketRush.Domain` | Aggregate, value object, domain event, `Result`/`Error`. Không phụ thuộc gì |
| `src/TicketRush.Application` | Use case, port (`IAppDbContext`, `IIdentityService`, `IEmailSender`, …), validator |
| `src/TicketRush.Infrastructure` | EF Core (Npgsql, snake_case, `xmin`), outbox, Redis, Identity, JWT, email, storage, dữ liệu mẫu |
| `src/TicketRush.Api` | Minimal API endpoint, xác thực JWT, middleware, ProblemDetails |
| `src/TicketRush.Worker` | Background service (outbox dispatcher, job chạy bằng leader election) |
| `src/TicketRush.MigrationService` | Chạy migration (kèm dữ liệu mẫu ở dev) một lần |
| `src/TicketRush.AppHost`, `src/TicketRush.ServiceDefaults` | Điều phối Aspire, OpenTelemetry, health check |
| `web/` | Next.js 16; `/api/*` được chuyển tiếp sang `api` (cùng origin) |
| `tests/` | Unit, architecture và integration test (.NET) |
| `load-tests/` | Kịch bản k6 bán vé, truy vấn SQL kiểm tra bất biến, compose overlay cho load test |
| `docs/runbooks/` | Quy trình vận hành (hoàn tiền bị treo: [parked-refunds.md](docs/runbooks/parked-refunds.md); giao email: [email-delivery.md](docs/runbooks/email-delivery.md); khoá QR của vé: [ticket-qr-keys.md](docs/runbooks/ticket-qr-keys.md)) |
| `specs/` | Đặc tả cho từng phần, từ 00 đến 17 |

## Triển khai production

`docker-compose.prod.yml` chạy toàn bộ stack trên một VPS, phía sau Caddy (TLS + HSTS):

```bash
cp .env.example .env        # điền các secret
docker compose -f docker-compose.prod.yml --env-file .env up -d
```

Khi bật lại trigger `push`, mỗi lần push lên `main` CI build và publish image lên GHCR (chạy tay thì không publish). CI đang tạm dừng (xem [Chạy CI ở máy trước khi push](#chạy-ci-ở-máy-trước-khi-push)), nên hiện tại build image ngay trên server bằng `--build`.

SignalR (`/hubs/*`) đi thẳng từ Caddy tới `api:8080`, không qua Next (bản `standalone` không proxy được WebSocket).
`CaddyWebSocketTests` kiểm tra điều này bằng thật: chạy image `caddy:2` với đúng `deploy/Caddyfile` (chỉ thay địa chỉ site
và upstream), mở kết nối SignalR chỉ dùng WebSocket qua Caddy tới api chạy trên Kestrel, rồi nhận một `SeatDelta`.
Cần Docker: `dotnet test --project tests/TicketRush.Api.IntegrationTests --filter-class "*CaddyWebSocketTests"`.

### Thanh toán ở production

- ⚠️ **Production chưa nhận được tiền thật.** Cổng thanh toán duy nhất hiện có là cổng giả lập, và nó **tắt mặc định** ở production (`Payments:Mock:Enabled` bị từ chối khi chạy ngoài Development, trừ khi đặt thêm `Payments:Mock:AllowInProduction=true` cho một bản demo công khai). Muốn bán vé thật cần viết adapter cho một cổng thật (spec 08, `IPaymentGateway`) và đăng ký nó.
- `docker-compose.prod.yml` đặt `Payments__Mock__WebBaseUrl` thành `https://${PUBLIC_HOST}`, để khi bật cổng giả lập cho demo, `redirectUrl` trỏ đúng tên miền công khai thay vì `http://localhost:3000`.
- Webhook của cổng thanh toán (`POST /api/v1/webhooks/payments/{provider}`) đi **`Caddy → api:8080` trực tiếp**: `deploy/Caddyfile` có khối `handle /api/v1/webhooks/*` cạnh `handle /hubs/*`, mọi đường dẫn khác vẫn tới `web:3000`. Chữ ký HMAC phủ đúng từng byte của thân request nên không để Next đứng giữa. Đã kiểm chứng trên stack compose (xem spec 08): qua rewrite `/api/:path*` của Next, thân request, header `X-Signature` và mã trạng thái (200, 401, 413, 404) giống hệt khi gọi thẳng api, **trừ** một thân hơi vượt giới hạn 64 KB (ví dụ 100 KB) đôi khi trả 502 thay vì 413 (api trả 413 rồi đóng kết nối khi Next còn đang ghi, `EPIPE`); vì vậy tuyến webhook được chuyển thẳng tới api. Cổng giả lập vẫn gọi thẳng `api:8080`, nên chỉ một provider thật mới đi qua tuyến Caddy này.
- Huỷ sự kiện hoàn tiền cho người mua **không cần** key ring Data Protection: việc xử lý đơn của sự kiện bị huỷ không đọc bí mật QR của vé.
- Giới hạn đã biết của việc hết hạn đơn và hoàn tiền: xem [docs/runbooks/parked-refunds.md](docs/runbooks/parked-refunds.md#known-limits-accepted).

### Độ bền của Redis

Ghế đang giữ và tập ghế đã bán nằm trong Redis (spec 07), nên Redis production chạy với
`--appendonly yes` (xem `docker-compose.prod.yml`). Mặc định `appendfsync everysec`: Redis ghi AOF xuống đĩa
mỗi giây, vì vậy nếu Redis sập đột ngột thì **mất tối đa khoảng 1 giây** lệnh ghi cuối (một ít lượt giữ ghế).
Đây là quyết định R3 của spec 07: chấp nhận cửa sổ mất dữ liệu này, không thêm replica và không đổi sang
`appendfsync always`.

Phần mất đi được sửa bằng hai lớp, vì Postgres mới là nguồn sự thật về ghế đã bán:

- **Reconciler** (`worker`, chạy lúc khởi động và mỗi 5 phút, thêm một lượt nhanh mỗi 5 giây để làm nóng lại sự kiện
  còn lạnh) dựng lại tập `sold` và số GA còn lại từ database, rồi ghi log cảnh báo nếu có lệch.
- **Unique index trên bảng vé** (spec 08) chặn bán trùng một ghế ngay cả khi Redis lỡ cho một lượt giữ lọt qua.

Nguồn dữ liệu "đã bán" của reconciler là **bảng `tickets`** (các vé chưa bị huỷ; `DbSoldInventorySource`), không còn là nguồn rỗng như trước spec 08. Vì vậy mất `sold` trong Redis luôn tự khôi phục được từ database. Mỗi đơn đã thanh toán được ghi vào Redis đúng một lần nhờ dấu `committed:<orderId>` (giữ 7 ngày), nên outbox giao lại `OrderPaid` cũng không làm đếm đôi.

Cả api và worker đều kiểm tra lúc khởi động (chỉ ngoài môi trường Development): nếu Redis không chạy với AOF
(`aof_enabled` trong `INFO persistence`) thì ghi log **Warning**. Khi không đọc được (ví dụ Redis do nhà cung cấp quản lý
chặn lệnh) thì chỉ ghi log Information và vẫn chạy bình thường. Không có AOF, mỗi lần Redis khởi động lại sẽ mất toàn bộ
lượt giữ ghế, không chỉ 1 giây cuối.

### Khoá Data Protection (bí mật QR của vé)

`Ticket.QrSecret` (32 byte ngẫu nhiên) được mã hoá khi lưu bằng ASP.NET Data Protection (mục đích `TicketRush.Tickets.QrSecret.v1`).
Bộ khoá (key ring) của Data Protection nằm **trong Redis**, ở key `tr:<môi trường>:dataprotection:keys` (một LIST các khoá XML), để mọi api và worker dùng chung.
Bộ khoá này cũng bảo vệ token xác nhận email và đặt lại mật khẩu.

> ⚠️ **Mất key ring này thì không giải mã được bí mật QR của mọi vé đã phát hành** (các vé trở thành không đọc được, không thể tạo mã QR). Token email đang chờ cũng hết hiệu lực.
> Các cách mất: xoá volume `redis-data`, `FLUSHALL`, chạy Redis không có AOF rồi khởi động lại, đổi `Redis__Environment` (đổi tiền tố key).

Việc cần làm ở production:

- Redis phải bền: giữ `--appendonly yes` và volume `redis-data` (xem trên). Không dùng Redis chỉ-cache cho môi trường có vé thật.
- **Sao lưu key ring** (khoá chỉ đổi khoảng 90 ngày một lần, nên sao lưu hằng ngày là đủ). Mỗi phần tử của LIST là một dòng XML; sao lưu từng phần tử ra một tệp, bỏ ký tự xuống dòng mà `redis-cli --raw` thêm vào cuối (`head -c -1` của GNU coreutils; trên macOS dùng `perl -pe 'chomp if eof'`):

  ```bash
  DC="docker compose -f docker-compose.prod.yml"
  KEY=tr:prod:dataprotection:keys
  n=$($DC exec -T redis sh -c "redis-cli -a \"\$REDIS_PASSWORD\" --no-auth-warning LLEN $KEY")
  umask 077 && mkdir -p dp-keys
  for i in $(seq 0 $((n-1))); do
    $DC exec -T redis sh -c "redis-cli -a \"\$REDIS_PASSWORD\" --no-auth-warning --raw LINDEX $KEY $i" | head -c -1 > "dp-keys/$i.xml"
  done
  set -o pipefail    # nếu tar hoặc age lỗi thì không xoá bản rõ
  tar -c dp-keys | age -r <khoá công khai age> -o dp-keys.tar.age && rm -r dp-keys     # hoặc: gpg --encrypt -r <id>
  ```

- **Khôi phục** (key phải đang trống: kiểm tra `LLEN` bằng 0, nếu không sẽ có khoá lặp; làm **trước khi** cho api và worker chạy). `redis-cli -x` đọc nguyên tệp làm giá trị, nên XML có dấu nháy, `&` hay `/` không bị hỏng; giữ đúng thứ tự `0, 1, 2, ...`:

  ```bash
  # Khai báo lại biến: khôi phục thường chạy ở một shell mới.
  DC="docker compose -f docker-compose.prod.yml"
  KEY=tr:prod:dataprotection:keys
  umask 077
  age -d -i <khoá riêng> dp-keys.tar.age | tar -x
  n=$(ls dp-keys/*.xml | wc -l)
  [ "$n" -gt 0 ] || { echo "không có phần tử key nào để khôi phục"; exit 1; }
  for i in $(seq 0 $((n-1))); do
    $DC exec -T redis sh -c "redis-cli -a \"\$REDIS_PASSWORD\" --no-auth-warning -x RPUSH $KEY" < "dp-keys/$i.xml"
  done
  $DC exec -T redis sh -c "redis-cli -a \"\$REDIS_PASSWORD\" --no-auth-warning LLEN $KEY"   # phải bằng $n
  rm -r dp-keys    # không để bản rõ trên đĩa
  ```

  Đã kiểm chứng vòng sao lưu, `FLUSHALL`, khôi phục trên container `redis:7.4` dùng một lần (cùng dạng lệnh `redis-cli` qua `docker exec -i`, tương đương `compose exec -T`): hai phần tử XML có dấu nháy, `+/=` và chú thích giống key thật, mỗi phần tử khớp từng byte (`cmp`) với bản gốc. Chưa chạy trực tiếp trên stack compose.
- ⚠️ **Tệp sao lưu là khoá chủ ở dạng văn bản thường**: Data Protection không mã hoá key ring khi lưu (chính XML có dòng cảnh báo "the key below is in an unencrypted form"). Ai có tệp này giải mã được mọi bí mật QR và token. Hãy coi nó là **secret**: mã hoá (age/gpg) ngay khi tạo, không để bản rõ trên đĩa (`umask 077`, xoá thư mục tạm), không commit, không gửi qua chat, và cất ở nơi có quyền truy cập **tách biệt** với bản dump Postgres (người đọc được dump không đồng thời đọc được khoá).
- Sao lưu Postgres mà thiếu key ring thì các vé trong bản sao lưu đó không dùng lại được. Hai thứ phải đi cùng nhau.
- Spec 10 (vé điện tử) vẫn dùng bí mật ngẫu nhiên lưu mã hoá như trên (không suy ra từ khoá ký), nên key ring phải được coi như dữ liệu quan trọng ngang database: mất nó thì `GET /tickets/{id}/qr-key` trả 500. Xem [docs/runbooks/ticket-qr-keys.md](docs/runbooks/ticket-qr-keys.md).
