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
| [08](specs/08-checkout-and-payment.md) – [17](specs/17-load-testing-and-observability.md) | Thanh toán, vé điện tử… | ⏳ Chưa làm |

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
| `worker` | Background service: gửi outbox, gửi email |
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
| `/` | Trang chủ: sự kiện sắp mở bán kèm đếm ngược, danh mục, thành phố, sự kiện sắp diễn ra |
| `/events` | Danh sách sự kiện: tìm không dấu ("son tung"), lọc theo danh mục, thành phố, ngày, giá; "Xem thêm" |
| `/events/son-tung-mtp-live` | Trang chi tiết: nút mua vé theo trạng thái mở bán, "Thêm vào lịch" (.ics), bản đồ, bảng giá, FAQ |
| `/login`, `/register` | Đăng nhập, đăng ký (hiện/ẩn mật khẩu, thanh độ mạnh mật khẩu) |
| `/forgot-password`, `/reset-password` | Quên và đặt lại mật khẩu. Email nằm trong Mailpit, link có hiệu lực 1 giờ |
| `/verify-email` | Trang mở từ link xác nhận email |
| `/account` | Hồ sơ, số điện thoại, ảnh đại diện (JPEG/PNG/WebP/AVIF, tối đa 2 MB), đăng xuất |
| `/events/{slug}/seats` | Chọn ghế (cần lượt vào từ phòng chờ). Bấm "Mua vé" ở trang sự kiện → phòng chờ → tự chuyển sang trang này. Chọn ghế trên sơ đồ (chuột hoặc phím mũi tên + Enter), hoặc "Chọn chỗ tốt nhất", rồi "Tiếp tục" để giữ ghế 8 phút. Ghế người khác vừa giữ đổi trạng thái ngay, không cần tải lại |
| `/events/{slug}/queue` | Phòng chờ (cần đăng nhập và email đã xác nhận). Sự kiện mẫu "Hà Anh Tuấn" mở bán sau 5 phút, "Vũ Cát Tường" sau 20 phút: vào phòng chờ trước giờ mở để được xếp ngẫu nhiên, khi mở bán sẽ được nhận dần 50 người/giây (cấu hình `Queue` trong `appsettings.json`) |
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

# Frontend
cd web
npm run lint && npm run typecheck && npm test
ENABLE_DESIGN_PAGE=1 npm run build:e2e   # build kèm mock api (e2e/mock-api.mjs), giống CI
npx playwright install chromium     # lần đầu
npm run test:e2e                    # axe, font tiếng Việt, INP, đăng nhập, catalog (dùng mock api)
npm run lighthouse                  # budget LCP/TBT/CLS/JS (cần Chrome)
```

CI (`.github/workflows/ci.yml`) chạy tất cả các bước trên. Nó cũng kiểm tra API client không bị lệch so với
OpenAPI, và audit dependency: code ship ra production không được có lỗ hổng nào, công cụ dev không được có
lỗ hổng high/critical.

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
| `specs/` | Đặc tả cho từng phần, từ 00 đến 17 |

## Triển khai production

`docker-compose.prod.yml` chạy toàn bộ stack trên một VPS, phía sau Caddy (TLS + HSTS):

```bash
cp .env.example .env        # điền các secret
docker compose -f docker-compose.prod.yml --env-file .env up -d
```

Mỗi lần push lên `main`, CI build và publish image lên GHCR.

SignalR (`/hubs/*`) đi thẳng từ Caddy tới `api:8080`, không qua Next (bản `standalone` không proxy được WebSocket).
`CaddyWebSocketTests` kiểm tra điều này bằng thật: chạy image `caddy:2` với đúng `deploy/Caddyfile` (chỉ thay địa chỉ site
và upstream), mở kết nối SignalR chỉ dùng WebSocket qua Caddy tới api chạy trên Kestrel, rồi nhận một `SeatDelta`.
Cần Docker: `dotnet test --project tests/TicketRush.Api.IntegrationTests --filter-class "*CaddyWebSocketTests"`.

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

Cả api và worker đều kiểm tra lúc khởi động (chỉ ngoài môi trường Development): nếu Redis không chạy với AOF
(`aof_enabled` trong `INFO persistence`) thì ghi log **Warning**. Khi không đọc được (ví dụ Redis do nhà cung cấp quản lý
chặn lệnh) thì chỉ ghi log Information và vẫn chạy bình thường. Không có AOF, mỗi lần Redis khởi động lại sẽ mất toàn bộ
lượt giữ ghế, không chỉ 1 giây cuối.
