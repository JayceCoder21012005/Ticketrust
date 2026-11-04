# Spec 05: Organizer Event Management — design

Bổ sung cho [specs/05-organizer-event-management.md](../../../specs/05-organizer-event-management.md). Spec gốc quy
định **cái gì** (API, luật xuất bản, wizard 5 bước). Tài liệu này chốt **trải nghiệm** dựa trên phản hồi của nhà tổ
chức trên các trang review, cùng các quyết định kỹ thuật mà spec để mở.

## 1. Mục tiêu

Nhà tổ chức tạo được một sự kiện có sơ đồ ghế 600 chỗ và 2 đợt mở bán **trong vài phút, không sợ mất dữ liệu, không
sợ xuất bản nhầm**. Trước khi bấm "Xuất bản" họ biết chắc: trang người mua trông thế nào, giờ mở bán là mấy giờ
theo giờ Việt Nam, và những gì sẽ bị khoá sau đó.

**Không làm:** vẽ ghế kéo-thả, giá động (spec gốc loại trừ); nạp tồn kho Redis (`InventoryWarmer`, spec 07, ở đây
chỉ phát `EventPublished`); hoàn tiền khi huỷ (08); dashboard bán vé (15, mục sidebar chỉ là placeholder); nhập/xuất
file JSON trên UI (API vẫn nhận JSON đúng shape của spec).

**Phản mục tiêu:** wizard rối như Eventbrite; mất nội dung đã gõ; mở bán sai giờ vì nhầm múi giờ; sửa được giá/sơ đồ
khi đã mở bán; trang xem trước khác trang thật.

## 2. Phản hồi người dùng và cách đáp lại

| Phản hồi (nguồn) | Cách đáp lại trong TicketRush |
|---|---|
| Sơ đồ ghế là chỗ bị chê nhiều nhất: mất thời gian làm quen, khó vẽ hàng đều khi xếp nghiêng, phải chuyển qua lại giữa các công cụ (Capterra: SimpleTix, Showpass, seats.io) | Sơ đồ sinh từ form, không vẽ tay. Có **mẫu dựng sẵn** để bắt đầu. Bản xem trước SVG cập nhật ngay khi gõ, tính ở client |
| Khó sửa ảnh và nội dung sau khi xuất bản; giao diện rối (Capterra, G2: Eventbrite) | Sau khi xuất bản vẫn sửa được chữ và ảnh cover. Các trường bị khoá có icon ổ khoá kèm lý do. Mỗi bước wizard chỉ có một việc |
| Luma được khen "đơn giản, nhanh", tạo trang dưới 2 phút (TrustRadius, Luma) | **Tạo nhanh**: dialog 5 trường tạo ngay bản nháp, rồi mới vào wizard |
| Có checklist trước khi xuất bản (Ticket Tailor); xem trước trang người mua trên desktop và điện thoại (Humanitix); cảnh báo những thiết lập không đổi được sau khi xuất bản (Checkin Event) | Bước 5 gồm checklist ✓/✗ (mỗi mục có link về bước bị lỗi), khung xem trước thật và hộp "Sau khi xuất bản sẽ bị khoá" |
| Mở bán sai giờ do múi giờ hoặc làm tròn (diễn đàn The Events Calendar; Ticketmaster mở bán sớm 1 ngày) | Mọi giờ đều nhập và hiển thị theo **giờ Việt Nam (GMT+7)**, kèm thời gian tương đối ("mở bán sau 3 ngày 4 giờ"). Không làm tròn: giữ đúng phút đã nhập. **Dòng thời gian** các đợt bán |
| Mã presale gây bối rối (help center của Universe, Promotix, Ticket Fairy) | Tên đợt và giờ mở bán của từng đợt hiện rõ cho người mua (đã có từ 04). Phía organizer có nút "Tạo mã ngẫu nhiên" và cảnh báo mã không xem lại được sau khi lưu |

## 3. Trải nghiệm

### 3.1 Khung trang `/organizer`

- Dùng shadcn `Sidebar`. Mục **Sự kiện** và **Địa điểm** dùng được; **Dashboard** (15) và **Cài đặt** chỉ hiện
  nhãn "Sắp có" và không bấm được.
- Header có breadcrumb, ví dụ: Sự kiện › Sơn Tùng M-TP Live › Sơ đồ.
- Người không có role `Organizer` thấy trang "Bạn cần tài khoản nhà tổ chức", không phải trang trống. API vẫn trả 403.

### 3.2 Danh sách sự kiện

- Bảng gồm: tên, ngày diễn, địa điểm, trạng thái (badge Nháp/Đã xuất bản/Đã kết thúc/Đã huỷ) và số ghế.
- Ô tìm theo tên. Tham số `q` gửi lên server, dùng ILIKE bỏ dấu với hàm `immutable_unaccent` có từ 04.
- Lọc theo trạng thái bằng tab.
- Nút **"Tạo sự kiện"** mở dialog tạo nhanh: tên, danh mục, địa điểm (combobox, có link "Thêm địa điểm"), giờ bắt
  đầu, giờ kết thúc. Bấm "Tạo bản nháp" sẽ gọi `POST /organizer/events` rồi chuyển sang bước 1 của wizard.

### 3.3 Wizard `/organizer/events/{id}/{step}`

`step` là một trong `info | tiers | layout | phases | review`. Mỗi bước là một URL riêng, nên reload hay chia sẻ
link đều đúng bước.

- **Stepper bên trái:** trạng thái của từng bước (✓ xong, ! còn lỗi, ○ chưa làm) lấy từ kết quả checklist. Trên
  điện thoại, stepper chuyển thành thanh ngang ở trên cùng.
- **Tự lưu:**
  - Mỗi bước có endpoint `PUT` riêng.
  - Form hợp lệ thì lưu sau 800 ms kể từ lần gõ cuối, hoặc ngay khi rời bước.
  - Chỉ báo ở header: "Đang lưu…", "Đã lưu lúc 14:02", "Chưa lưu: còn 2 lỗi" hoặc "Lưu thất bại · Thử lại".
  - Đóng tab khi còn thay đổi chưa lưu thì trình duyệt hỏi lại (`beforeunload`).
- **Xung đột giữa hai tab:** event có concurrency token (xmin). Khi bị xung đột, API trả 409
  `event.concurrent_edit` và UI báo "Sự kiện vừa được sửa ở nơi khác · Tải lại".
- **Sự kiện đã xuất bản:**
  - Các bước Hạng vé, Sơ đồ và Mở bán chuyển sang chế độ chỉ xem, kèm banner ổ khoá: "Đã khoá sau khi xuất bản
    để không thay đổi tồn kho đang bán. Muốn đổi, hãy huỷ và tạo lại sự kiện."
  - Ở bước Thông tin, chỉ khoá danh mục, địa điểm và giờ.

**Bước 1 – Thông tin**
- Tên, danh mục, giờ bắt đầu và kết thúc, địa điểm, lineup (nhập dạng tag).
- Mô tả: ô nhập có tab **Soạn / Xem trước**. Định dạng là văn bản thuần, dòng trống tách đoạn, giống cách trang chi
  tiết 04 hiển thị. Không dùng Markdown, nên không có rủi ro XSS và không phải đổi renderer của 04.
- **Ảnh cover:**
  - Chọn ảnh rồi cắt theo khung **16:9** trong dialog (kéo để di chuyển, thanh trượt để zoom). Cắt bằng canvas, không
    thêm thư viện.
  - Xuất ra JPEG 1600×900, chất lượng 0.85.
  - Kiểm tra ≤ 5 MB trước khi xin URL upload.
  - Upload thẳng lên storage qua `POST cover-upload-url`, rồi lưu `coverImageUrl`.
  - Có thanh tiến trình. Lỗi thì hiện lý do (quá lớn, sai định dạng, link hết hạn).

**Bước 2 – Hạng vé**
- Bảng sửa trực tiếp: tên, giá (VND, có dấu phân cách nghìn), loại (Ngồi/Đứng), sức chứa (chỉ cho loại Đứng), màu.
  Màu chọn từ 8 màu có sẵn đạt tương phản với chữ trắng.
- Hạng Ngồi hiện "Số ghế: 240 (từ sơ đồ)", không có ô nhập.
- Xoá một hạng đang được khu nào đó dùng sẽ bị chặn với lỗi `tier.in_use`, kèm tên các khu đang dùng hạng đó.

**Bước 3 – Sơ đồ**
- Trái: danh sách khu, mỗi khu một card form (mã khu, hạng vé, kiểu Ngồi/Đứng, số hàng, ghế/hàng, gốc x/y, độ cong).
- Phải: **bản xem trước SVG** của sân khấu và các khu, tô màu theo hạng vé. Bên dưới có tổng số ghế theo từng hạng.
- Bấm vào một khu trên bản xem trước sẽ cuộn tới form của khu đó và làm nổi khu đó lên.
- **Mẫu dựng sẵn** (nút "Bắt đầu từ mẫu"): "Nhà hát 3 khu", "Sân khấu + khu đứng", "Sân vận động 4 phía". Mẫu chỉ
  điền form. Khu nào chưa có hạng vé thì được gán hạng đầu tiên cùng loại; nếu chưa có hạng nào cùng loại, UI nhắc
  tạo ở bước 2.
- Bản xem trước tính ở client bằng bản TypeScript của bộ sinh ghế (xem §4.2), không gọi API khi đang gõ. Lưu thì gọi
  `PUT layout`, server sinh lại `EventSeat`.

**Bước 4 – Mở bán**
- Phía trên là **dòng thời gian** ngang từ hôm nay tới ngày diễn. Mỗi đợt là một khối có nhãn tên, ngày diễn là một
  vạch. Đợt chồng giờ tô đỏ ngay khi nhập. Đợt mở trong vòng 10 phút tới tô vàng kèm cảnh báo.
- Mỗi đợt là một card: tên, giờ mở, giờ đóng (chọn ngày và giờ, ghi rõ "giờ Việt Nam"), bật/tắt phòng chờ, số vé
  tối đa mỗi người (1–8), mã presale (tuỳ chọn).
- **Mã presale:**
  - Có nút "Tạo mã ngẫu nhiên" (8 ký tự, bỏ các ký tự dễ nhầm như 0/O, 1/I).
  - Ô nhập có ghi chú: "Mã được mã hoá khi lưu, sau đó không xem lại được. Hãy sao chép trước khi lưu."
  - Đợt đã có mã thì hiện "Đã đặt mã · Đổi mã · Bỏ mã".

**Bước 5 – Kiểm tra & Xuất bản**
- **Checklist:** mỗi luật `IPublishRule` là một dòng ✓/✗. Dòng ✗ có lý do và link "Sửa" về đúng bước.
- **Xem trước:** khung iframe tới `/organizer/events/{id}/preview`, render trang chi tiết thật của 04 bằng dữ liệu
  nháp. Có nút chuyển Desktop (1280 px, thu nhỏ cho vừa khung) và Điện thoại (390 px). Dùng iframe để breakpoint
  theo viewport hoạt động đúng như trang thật.
- **Hộp cảnh báo** liệt kê những gì sẽ bị khoá: giá, sức chứa, sơ đồ, đợt bán.
- Nút **"Xuất bản"** chỉ bấm được khi checklist toàn ✓. Bấm thì mở dialog xác nhận tóm tắt: số ghế, số hạng, giờ mở
  bán đầu tiên (giờ Việt Nam và thời gian tương đối). Xuất bản xong chuyển về trang sự kiện kèm toast "Đã xuất bản ·
  Xem trang công khai".

**Huỷ sự kiện:** nút ở trang sự kiện, áp dụng được cho cả nháp lẫn đã xuất bản. Dialog yêu cầu gõ đúng tên sự kiện
mới bật nút "Huỷ sự kiện". Với sự kiện đã xuất bản, dialog nói rõ "Người mua sẽ được hoàn tiền (spec 08)".

### 3.4 Địa điểm `/organizer/venues`

- Bảng gồm tên, địa chỉ, thành phố và sức chứa. Nút "Thêm địa điểm" mở dialog với các trường tên, địa chỉ, thành
  phố, link bản đồ (chỉ nhận http/https, dùng `externalHttpUrl` của 04) và sức chứa.
- Địa điểm từ dữ liệu mẫu là **địa điểm dùng chung**: mọi organizer đều thấy và dùng được, nhưng không sửa được.

## 4. Quyết định kỹ thuật

### 4.1 Domain

- **Venue:** thêm `OrganizerId?`. `null` là địa điểm dùng chung (seed); có giá trị là địa điểm riêng của organizer
  đó. Danh sách venue của một organizer gồm địa điểm dùng chung và địa điểm riêng của họ. Tạo sự kiện với venue của
  organizer khác thì trả 404.
- **Event:**
  - Lưu `Layout` dạng jsonb: định nghĩa các khu đúng shape của spec, để mở lại form được. `EventSeat` được sinh ra
    từ nó.
  - Thêm các method sau, chỉ gọi được khi còn ở trạng thái Draft, nếu không thì ném lỗi `event.not_editable`:
    - `ReplaceTiers(...)`: upsert theo `id`; hạng không có trong danh sách gửi lên thì bị xoá.
    - `ReplaceLayout(...)`.
    - `ReplacePhases(...)`: kiểm tra chồng giờ, nếu có thì ném `event.phase_overlap`.
    - `UpdateSchedule(...)`.
  - `UpdateDetails` vẫn chạy được khi đã Published. Đổi chữ hoặc ảnh của sự kiện đã xuất bản sẽ phát
    `EventDetailsChanged` để làm mới cache.
- **Concurrency:** `xmin` làm concurrency token của `Event`.
  - Publish bị xung đột: tải lại; nếu sự kiện đã Published thì trả 200. Vì vậy hai lần publish đồng thời chỉ tạo
    **đúng một** outbox message `EventPublished`, đáp ứng acceptance "kể cả khi retry".
  - Các lệnh sửa bị xung đột: trả 409 `event.concurrent_edit`.
- **Mã presale:** `SHA-256(pepper ‖ code)` dạng hex. Pepper lấy từ cấu hình `Catalog:AccessCodePepper`:
  - Lúc dev lấy từ user-secrets hoặc tham số AppHost; khi triển khai lấy từ env. Không đưa vào git.
  - Thiếu pepper ngoài môi trường Development thì app không khởi động.
  - Khi `PUT phases`, mỗi đợt có `accessCode`: `null` giữ hash cũ (khớp theo `id`), `""` bỏ mã, có giá trị thì đặt
    mã mới. Mã được trim và so khớp không phân biệt hoa thường (chuẩn hoá về chữ hoa trước khi hash).

### 4.2 Bộ sinh sơ đồ ghế

- **Hệ toạ độ:** canvas ảo 1000 × 700 đơn vị, sân khấu ở trên. Khoảng cách giữa hai ghế cạnh nhau là 24, giữa hai
  hàng là 28.
- **Vị trí ghế:**
  - `origin` là góc trên trái của khu.
  - Ghế thứ `n` (từ 0) của hàng `r` có `x = origin.x + n·24` và `y = origin.y + r·28 + sag(n)`.
  - `sag(n) = curve · W · (1 − (2t − 1)²)` với `t = n / (seatsPerRow − 1)` và `W = (seatsPerRow − 1)·24`, tạo thành
    hàng cong võng xuống giữa. `curve` nằm trong 0..0.5.
  - Toạ độ làm tròn tới 2 chữ số thập phân.
- **Nhãn:** hàng đặt tên A, B, … Z, AA, AB… Ghế đánh số từ 1.
- **Index:** từ 0 tới n−1, theo thứ tự (khu theo thứ tự khai báo, hàng, số ghế).
- **Khu đứng** (`kind: "standing"`) không sinh ghế. Hạng của nó phải là loại GA.
- **Kiểm tra** (sai thì trả `layout.invalid` kèm `errors[]` theo từng khu):
  - Mã khu duy nhất, gồm 1–8 ký tự chữ hoặc số.
  - `tierId` thuộc sự kiện và đúng loại (khu ngồi dùng hạng Ngồi, khu đứng dùng hạng GA).
  - `rows` từ 1 tới 100, `seatsPerRow` từ 1 tới 100.
  - Tổng số ghế ≤ 20.000.
- **Hai bản cài đặt:** C# (nguồn sự thật, chạy ở server) và TypeScript (để xem trước). Một file fixture JSON
  chung kiểm tra cả hai cho ra cùng toạ độ, nhãn và index, cùng cách với test đối chiếu danh mục ở 04.
- **Hiệu năng:**
  - Khu 20×30 cho ra 600 ghế. Có test đo thời gian tính cộng render SVG dưới 100 ms.
  - Bản xem trước vẽ mỗi ghế bằng một `<circle>`, không có handler trên từng ghế.
  - Lưu xuống DB bằng một lệnh bulk insert. Ghế cũ được xoá trong cùng transaction.

### 4.3 Xuất bản

- `PublishChecklist` chạy các luật `IPublishRule`. Mỗi luật là một class nhỏ và trả về `{ code, step, message }`:
  - có ảnh cover;
  - có ≥ 1 hạng vé;
  - mỗi hạng Ngồi có ≥ 1 ghế;
  - mỗi hạng GA có sức chứa > 0;
  - có ≥ 1 đợt bán;
  - đợt đầu tiên mở ≥ 10 phút sau thời điểm hiện tại (giờ server);
  - các đợt không chồng nhau.
- `GET /organizer/events/{id}/checklist` trả kết quả để stepper và bước 5 hiển thị.
- `POST publish` khi còn lỗi trả 422 `event.publish_checklist_failed` kèm `failures[]`. Nếu có đợt chồng giờ thì
  `failures[]` chứa `event.phase_overlap`. Acceptance yêu cầu "liệt kê luật bị lỗi".
- Publish thành công: `EventPublished` vào outbox trong cùng transaction.
  - API xoá luôn HybridCache catalog của chính nó qua `ICatalogCacheInvalidator`.
  - Worker xử lý outbox message và gọi `POST /api/revalidate` của web, có retry qua outbox. Handler này cũng xử lý
    `EventCancelled` và `EventDetailsChanged`.
  - `InventoryWarmer` (07) sau này chỉ cần đăng ký thêm handler.

### 4.4 Quyền

- Mọi route `/organizer/*` dùng policy `RequireOrganizer`.
- Ownership được lọc ngay trong query (`OrganizerId == currentUser`). Sự kiện của người khác trả 404
  (`event.not_found`), không phải 403.

### 4.5 Web

- Route group `(organizer)/organizer/...`. Đây là trang ứng dụng, không nằm trong budget Lighthouse công khai của 04,
  nhưng vẫn qua axe (sáng/tối).
- Dữ liệu đi qua TanStack Query. Client API sinh từ OpenAPI như các spec trước.
- Form dùng react-hook-form với zod. Schema tạo từ cùng giới hạn với server (độ dài, 1–8 vé, …).
- Giờ được nhập dưới dạng ngày cộng giờ theo giờ Việt Nam, rồi đổi sang `DateTimeOffset +07:00` trước khi gửi.
  Không bao giờ dùng múi giờ của máy người dùng.

## 5. Phần để người làm tự quyết

Vị trí cụ thể các khu trong mẫu dựng sẵn; 8 màu hạng vé; chữ trên các nút; tên file và component; cách chia task.

## 6. Các quyết định mình tự đưa ra (bạn xem lại)

1. Địa điểm từ dữ liệu mẫu dùng chung cho mọi organizer; địa điểm tạo mới thuộc riêng người tạo.
2. Mô tả là văn bản thuần, không dùng Markdown.
3. Bỏ nhập/xuất JSON trên UI. API vẫn nhận JSON.
4. Hai tab cùng sửa thì tab sau nhận 409, không ghi đè lặng lẽ.
5. Mã presale không phân biệt hoa thường.
6. Ghế cách nhau 24 đơn vị, hàng cách nhau 28; độ cong võng xuống giữa; tối đa 20.000 ghế mỗi sự kiện.
7. Bước 5 xem trước bằng iframe tới route preview riêng, không render component lồng trong trang.
