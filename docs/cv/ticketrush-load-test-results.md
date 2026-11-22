# TicketRush: kết quả load test và observability

Tài liệu tổng hợp số liệu đo thật từ lần load test ngày 22/11/2026 (spec 17), dùng để đưa vào CV, portfolio và chuẩn bị phỏng vấn. Mọi con số dưới đây lấy từ `web/src/features/lab/results.json` và hiển thị công khai trên trang `/lab` của dự án.

---

## 1. Tóm tắt một đoạn

TicketRush là hệ thống bán vé flash sale (phòng chờ ảo, giữ ghế, thanh toán, xuất vé). Tôi xây bộ load test 5 kịch bản bằng k6 và hệ thống observability đầy đủ (OpenTelemetry, Prometheus, Tempo, Grafana), rồi chạy thật trên một laptop. Qua **hơn 375.000 request**, hệ thống có **0 lỗi ngoài dự kiến**, **không bán trùng một ghế nào** và mọi bất biến dữ liệu đều đúng, kể cả khi tôi cố tình tắt worker leader, khởi động lại Redis và dừng Postgres 10 giây giữa lúc đang bán. Load test cũng tìm ra 2 lỗi thật mà test thường không bắt được; cả hai đã được sửa và có test chống tái phát.

---

## 2. Gợi ý gạch đầu dòng cho CV

### Tiếng Anh (khuyên dùng cho CV)

- Built a 5-scenario k6 load-testing suite (sale-open spike, buy flow, webhook storm, 30-minute soak, chaos) for a flash-sale ticketing platform; ran **375K+ requests with zero unexpected errors and zero double-sold seats**.
- Kept the waiting-room status endpoint at **p99 9.6 ms** (budget 50 ms) under a **2,000-user** sale-open spike, ~290 req/s sustained.
- Proved payment idempotency under a **webhook storm** (every webhook delivered 6×, in parallel and delayed): **tickets issued exactly once per order**.
- Ran **chaos tests**: killed the worker leader, restarted Redis, and paused PostgreSQL for 10 s mid-sale; the system recovered on its own and the wave of purchases after it saw **0 errors**.
- A **30-minute soak test** (115K requests) showed stable memory and GC heap, and Redis hold keys returned to 0.
- Instrumented the system end to end with **OpenTelemetry**:
  - 10 business metrics, and a Grafana dashboard with 4 alerts.
  - **Distributed tracing** that follows one purchase across the API, the outbox, the payment gateway and the email worker as a single trace (119 spans).
- Used load testing to find and fix a **production-grade bug**: a slow downstream HTTP call (default retry budget of 30 s) stalled the transactional outbox, blocking inventory commits, emails and refunds. Fixed it with a single 3 s attempt, relying on the outbox's own retry, and pinned it with an integration test.
- Automated **invariant checks** after every scenario: no oversold seat, no duplicate ticket, per-user ticket limit respected, Redis inventory reconciled with PostgreSQL.

### Bản ngắn (nếu CV chỉ còn 2–3 dòng)

- Designed and ran k6 load and chaos tests for a flash-sale ticketing system (.NET, PostgreSQL, Redis): 375K+ requests, 0 unexpected errors, 0 oversold seats, p99 9.6 ms on the waiting-room endpoint at 2,000 concurrent users.
- Built full observability with OpenTelemetry, Prometheus, Tempo and Grafana, including a distributed trace following one purchase from seat hold to email; load tests found and fixed an outbox-stall bug.

### Tiếng Việt

- Xây bộ load test 5 kịch bản bằng k6 cho hệ thống bán vé flash sale. Hơn 375.000 request, 0 lỗi ngoài dự kiến, không bán trùng ghế.
- Endpoint trạng thái phòng chờ đạt p99 9,6 ms (ngân sách 50 ms) với 2.000 người dùng đồng thời lúc mở bán.
- Chaos test: tắt worker leader, khởi động lại Redis, dừng PostgreSQL 10 giây. Hệ thống tự phục hồi, đợt mua sau đó không có lỗi nào.
- Observability đầy đủ bằng OpenTelemetry, Prometheus, Tempo và Grafana. Có distributed trace theo một lượt mua từ giữ ghế tới gửi email.

---

## 3. Bảng số liệu chi tiết

**Máy chạy:** một laptop chạy toàn bộ.

| Thành phần | Cấu hình |
|---|---|
| CPU | Intel Core i5-11400H, 12 luồng |
| RAM | 15,8 GB |
| Docker | được cấp 7,6 GB RAM |
| Load generator | k6 v2.2.0 |

Stack ứng dụng, cơ sở dữ liệu, bộ observability và k6 đều dùng chung CPU của laptop này.

| Kịch bản | Mô tả | Quy mô | Số request | Độ trễ (p50 / p95 / p99) | Kết quả |
|---|---|---|---|---|---|
| 01 Spike mở bán | Người mua vào phòng chờ trước giờ mở bán, poll theo nhịp server chỉ định | 2.000 người dùng ảo, 11 phút | 195.254 (~292 req/s) | Queue status: 3,2 / 5,3 / **9,6 ms** | Đạt ngân sách p99 < 50 ms, 0 lỗi |
| 02 Luồng mua | Giữ ghế (30% tranh cùng ghế "nóng") → tạo đơn → thanh toán, có double-click | 100 đồng thời, 2.000 lượt mua | 39.850 | Hold: 19 / 318 / 620 ms; Checkout: 14 / 203 / 923 ms | 0 lỗi, double-click luôn nhất quán; **chưa đạt** ngân sách p99 (150/300 ms) |
| 03 Bão webhook | Mỗi webhook thanh toán gửi 3 lần song song, rồi 3 lần nữa sau 0–5 phút | 300 người mua | 4.557 | 11 / 258 / 425 ms | 0 webhook bị bỏ sót, **mỗi đơn xuất vé đúng 1 lần** |
| 04 Soak | Tải đều 30 phút, một nửa lượt giữ ghế để tự hết hạn | 3.298 lượt, 30 phút | 115.429 | Hold: 9,6 / 15 / 51 ms; Checkout: 8,2 / 17 / 35 ms | Bộ nhớ và GC heap ổn định; hold key Redis đạt đỉnh 90, về 0 khi hết tải |
| 05 Chaos | Luồng mua trong khi bơm lỗi hạ tầng | 1.298 lượt | 20.069 | Hold p99 56 ms; Checkout p99 63 ms | Tự phục hồi, đợt mua sau sự cố có **0 lỗi** |
| **Tổng** | | | **375.159** | | **0 lỗi ngoài dự kiến** |

**Lịch bơm lỗi ở kịch bản 05:**

| Giây thứ | Sự kiện |
|---|---|
| 47 | Tắt cưỡng bức worker đang giữ leader lock |
| 101 | Khởi động lại Redis |
| 160–171 | Dừng PostgreSQL 10 giây |

**Soak 30 phút, chi tiết:**

| Chỉ số | Kết quả |
|---|---|
| Bộ nhớ API | khoảng 410–430 MB, xu hướng −6,6 MB / 10 phút (không rò rỉ) |
| Bộ nhớ worker | +8 MB / 10 phút, tổng của 2 worker |
| GC heap của worker | −0,2 MB / 10 phút |

**Bất biến được kiểm tra tự động sau mỗi kịch bản.** Có 9 câu SQL, đều kiểm tra hai lần: ngay sau khi chạy và khi hệ thống đã lắng. Cả 5 kịch bản đều đúng hết.

- Không ghế nào bán quá 1 lần, không vé trùng.
- Số vé GA không vượt sức chứa.
- Mỗi người không vượt giới hạn vé.
- Đơn đã trả luôn có vé, đơn hoàn tiền có trạng thái khớp.
- Tồn kho trong Redis khớp với PostgreSQL.

**Distributed trace thật lấy từ Tempo.** Một lượt mua tạo 1 trace liên tục, gồm 119 span trên 2 service.

| Bước | Service | Thời điểm |
|---|---|---|
| Giữ ghế | api | 0 ms |
| Tạo đơn | api | 9 ms |
| Thanh toán | api | 23 ms |
| Cổng thanh toán gửi webhook | worker | 872 ms |
| Xử lý webhook | api | 872 ms |
| Outbox `OrderPaid` | worker | 1.100 ms |
| Gửi email xác nhận | worker | 1.451 ms |

---

## 4. Lỗi thật mà load test tìm ra

Đây là phần nên kể khi phỏng vấn, vì nó cho thấy load test có giá trị thật.

### Outbox bị kẹt bởi một lệnh gọi HTTP chậm

- **Triệu chứng:** ở lần chạy bão webhook đầu tiên, sau khi hết tải 1 phút vẫn còn 59 message outbox chưa xử lý, và Redis chưa ghi nhận ghế nào đã bán.
- **Nguyên nhân gốc:**
  - Sau khi sự kiện được publish, hệ thống gọi HTTP để làm mới cache trang web. Host web không phản hồi.
  - HTTP client mặc định tự thử lại trong ngân sách 30 giây.
  - Outbox xử lý message tuần tự, nên việc trừ tồn kho, gửi email và hoàn tiền đều xếp hàng phía sau lệnh gọi đó.
- **Cách sửa:**
  - Lệnh gọi này chỉ được thử 1 lần, timeout 3 giây, không retry phía client. Outbox đã có cơ chế retry riêng với backoff.
  - Thêm integration test với một host nhận kết nối nhưng không bao giờ trả lời.
  - Thêm gauge đo độ trễ outbox và cảnh báo Grafana khi độ trễ vượt 30 giây.
- **Bài học:** retry chồng retry ở nhiều tầng, cộng với xử lý tuần tự, biến một dependency phụ thành điểm nghẽn của cả hệ thống.

### Metric đếm sai khi handler khác thất bại

- **Lỗi:** handler đếm "đơn đã thanh toán" chạy trước các handler nghiệp vụ. Khi một handler phía sau lỗi, message được retry và đơn bị đếm thêm lần nữa.
- **Cách sửa:** đăng ký metric handler sau cùng, để mỗi message chỉ được đếm một lần, sau khi mọi việc thật đã xong. Có test kiểm tra thứ tự đăng ký.

### Postgres hết kết nối ở 100 người mua đồng thời

- **Lỗi:** 2 service, mỗi service có pool tối đa 100 kết nối, trong khi `max_connections` mặc định của Postgres là 100.
- **Cách sửa:** tăng `max_connections` lên 300. Quy tắc định cỡ: pool size × số service phải nhỏ hơn `max_connections`.

---

## 5. Observability đã xây

| Hạng mục | Chi tiết |
|---|---|
| Metric nghiệp vụ (10) | Lượt giữ ghế theo kết quả, giữ ghế hết hạn, người được vào từ phòng chờ, độ dài hàng đợi, đơn đã trả, yêu cầu hoàn tiền, thời gian và tỉ lệ lỗi khi gọi cổng thanh toán, độ trễ outbox, message chết, số lần sửa lệch tồn kho |
| Tracing | W3C `traceparent` được lưu qua outbox, hàng đợi email và mock gateway, nên một lượt mua bất đồng bộ vẫn là 1 trace liền |
| Dashboard | Grafana "Flash sale", 4 cảnh báo (outbox trễ quá 30 s, có message chết, p95 cổng thanh toán quá 3 s, phải sửa lệch tồn kho trong lúc bán) |
| Hạ tầng | OpenTelemetry Collector, Prometheus, Tempo, Grafana bằng Docker Compose, chạy bằng 1 lệnh `make load-test` |

---

## 6. Chất lượng code đi kèm

| Bộ test | Số lượng | Kết quả |
|---|---|---|
| Domain unit tests | 374 | Đạt hết |
| Application unit tests | 229 | Đạt hết |
| Architecture tests | 15 | Đạt hết |
| Integration tests (Postgres và Redis thật qua Testcontainers) | 1.200 | Đạt hết |
| Frontend unit tests (Vitest) | 1.234 | Đạt hết |
| E2E (Playwright, kèm kiểm tra accessibility bằng axe) | 263 | Đạt hết |

**Tech stack:**

| Phần | Công nghệ |
|---|---|
| Backend | .NET / ASP.NET Core, EF Core |
| Dữ liệu | PostgreSQL, Redis (Lua script cho giữ ghế atomic) |
| Messaging | Transactional outbox |
| Frontend | Next.js |
| Load và chaos test | k6 |
| Observability | OpenTelemetry, Prometheus, Tempo, Grafana |
| Hạ tầng | Docker Compose |

---

## 7. Lưu ý trung thực khi trình bày

Nhà tuyển dụng đánh giá cao sự minh bạch. Nên nói rõ ba điểm sau nếu được hỏi:

- **Quy mô laptop.** Kịch bản spike chạy 2.000 người dùng ảo, thay vì 20.000 như spec thiết kế, vì toàn bộ hệ thống và k6 chạy chung một laptop.
- **Ngân sách độ trễ của luồng mua chưa đạt** ở 100 người mua đồng thời: p99 giữ ghế 620 ms (mục tiêu 150 ms), checkout 923 ms (mục tiêu 300 ms).
  - Ở tải đều của soak, hai con số này là 51 ms và 35 ms.
  - Nguyên nhân là tranh chấp CPU trên cùng một máy. Kết quả được ghi là "không đạt" chứ không nới ngưỡng.
  - Tính đúng đắn không bị ảnh hưởng: mọi bất biến đều đúng.
- **Toàn bộ người dùng ảo đến từ 1 địa chỉ IP,** nên giới hạn tốc độ theo IP được nới trong lúc test. Mọi giới hạn theo người dùng (phòng chờ, giữ ghế, checkout) vẫn giữ như production.

**Câu trả lời mẫu khi được hỏi "Bạn sẽ làm gì để đạt ngân sách độ trễ?":**

> Đầu tiên tách load generator và observability ra máy riêng để có số đo sạch. Sau đó dùng trace trong Tempo để xem p99 nằm ở đâu, thường là chờ kết nối DB hoặc khoá hàng. Từ đó mới quyết định tinh chỉnh pool, index, hay scale ngang API, vì API stateless và tồn kho nằm trong Redis.
