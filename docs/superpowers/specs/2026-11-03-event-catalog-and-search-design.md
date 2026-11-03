# Spec 04: Event Catalog & Search — design

Bổ sung cho [specs/04-event-catalog-and-search.md](../../../specs/04-event-catalog-and-search.md). Spec gốc quy định
**cái gì** (API, search, cache, CTA state). Tài liệu này chốt **trông thế nào và cảm giác ra sao**, dựa trên
các trang tham khảo, cùng những quyết định kỹ thuật mà spec để mở.

## 1. Mục tiêu

Người dùng mở TicketRush phải biết ngay **sự kiện nào sắp mở bán, còn bao lâu, giá từ bao nhiêu**, rồi tới trang chi
tiết với nút hành động duy nhất, rõ ràng. Đây là site flash-sale: thứ quan trọng nhất là **thời điểm mở bán**, không
phải "khám phá thư giãn".

**Không làm** trong spec này: phòng chờ (06), chọn ghế (07), "Nhắc tôi" thật (09), danh sách chờ (13), giao diện
organizer (05). Các CTA đó chỉ là link/nút placeholder.

**Phản mục tiêu:** trang đẹp nhưng vượt budget hiệu năng của spec 02; danh sách phải cuộn mãi mới thấy sự kiện mở
bán sớm; đếm ngược lệch giờ do đồng hồ máy người dùng sai.

## 2. Tham khảo và những gì lấy từ mỗi trang

| Trang | Lấy gì | Không lấy gì |
|---|---|---|
| **Ticketbox** (thị trường VN) | Giá dạng "Từ 800.000 ₫" nổi bật trên card; các hàng theo chủ đề trên trang chủ; danh mục quen thuộc với người Việt | Banner carousel tự chạy (hại LCP, khó dùng), trang chủ quá dài |
| **Luma** (trang chi tiết) | Bố cục 2 cột: ảnh cover vuông bên trái, thông tin bên phải; ngày giờ và địa điểm hiển thị dạng **ô icon + 2 dòng** (ô lịch nhỏ có tháng/ngày); chrome đơn sắc, màu đến từ ảnh; card đăng ký nổi bật | Theme tuỳ biến theo sự kiện |
| **DICE** | **Một màu nhấn duy nhất cho hành động chính** (mua vé, phòng chờ); chữ tương tác đậm, chữ thông tin xám; lineup được trình bày như nội dung chính | Nền đen bắt buộc (ta giữ light/dark theo hệ thống) |
| **Eventbrite** | Badge khẩn cấp trên card ("Sắp mở bán", "Đang mở bán", "Sắp hết vé"); bộ lọc ngày nhanh "Hôm nay / Cuối tuần này / Tháng này" | Giá ẩn trên card ("Check ticket price on event") |
| **Critique DICE (Pratt IxD)** | Bộ lọc phải là **chip có thể sửa/xoá tại chỗ** và luôn hiện trạng thái đang lọc (thành phố hiện tại) | Trang chủ cuộn dài không có điểm neo |

## 3. Ngôn ngữ thị giác

Giữ nguyên design system của spec 02 (token màu, font, `--primary` tím). Bổ sung:

- **Poster tự sinh khi chưa có ảnh cover:** gradient + họa tiết theo danh mục, tiêu đề in lớn, sinh từ slug nên ổn định
  giữa các lần render. Không cần ảnh bản quyền, không tốn request ảnh, LCP là text. Dùng chung cho card, trang chi
  tiết và ảnh Open Graph.
- **Badge trạng thái bán** trên card và trang chi tiết: `Đang mở bán` (primary), `Mở bán 20:00 15/11` (outline),
  `Hết vé` (muted), `Đã huỷ`.
- **Tỉ lệ ảnh:** card 16:9 (giữ như spec 02), trang chi tiết vuông 1:1 (Luma).
- **Số và giờ** dùng `tabular-nums` để đếm ngược không bị nhảy chữ.

## 4. Các trang

### Trang chủ `/`

1. **Hero "Sắp mở bán":** sự kiện mở bán gần nhất (hoặc đang mở bán): poster lớn, tên, ngày, địa điểm, giá từ,
   **đếm ngược lớn** tới giờ mở bán, nút CTA. Không carousel.
2. **Chip danh mục** (cuộn ngang trên mobile) → `/events?category=…`.
3. **Hàng "Sắp mở bán"**: tối đa 8 sự kiện, card có đếm ngược trực tiếp, cuộn ngang (snap) trên mobile, lưới trên
   desktop.
4. **Chọn thành phố** + **lưới "Sự kiện sắp diễn ra"** (12 sự kiện) + nút "Xem tất cả" → `/events`.

Dữ liệu lấy server-side, `"use cache"` với `cacheTag("events:list")` + `cacheLife` 30 s. Phần đếm ngược là island client nhỏ.

### Danh sách `/events`

- **Desktop:** sidebar bộ lọc bên trái (danh mục, thành phố, khoảng ngày nhanh + tuỳ chọn, giá tối đa), lưới 3 cột.
- **Mobile:** nút "Bộ lọc (n)" mở `Sheet`.
- **Thanh chip bộ lọc đang áp dụng** ở trên kết quả, mỗi chip có nút ×, cộng "Xoá tất cả".
- Ô tìm kiếm debounce 300 ms, cập nhật URL bằng `router.replace` (không thêm history mỗi phím).
- Mọi bộ lọc nằm trong URL, nên link chia sẻ được. Trang đầu render trên server; "Xem thêm" tải trang kế bằng cursor
  (TanStack `useInfiniteQuery`).
- Không có kết quả: `EmptyState` + gợi ý xoá bộ lọc.

### Chi tiết `/events/[slug]` (Luma)

- **Desktop 2 cột:** trái là poster vuông (LCP, `priority`), dưới là ban tổ chức. Phải là danh mục, tiêu đề, **ô ngày**
  (ô lịch tháng/ngày + "Thứ Bảy, 15/11/2026 · 19:30 – 22:30" + nút "Thêm vào lịch" tải `.ics`), **ô địa điểm**
  (tên + địa chỉ + link bản đồ), **card mua vé** (trạng thái CTA + giá từ + đếm ngược).
- Dưới: **Lineup** (chip nghệ sĩ), **Giới thiệu** (markdown), **Bảng giá vé** (màu tier + tên + giá + loại
  ngồi/đứng), **Các đợt mở bán** (tên, thời gian, "Có phòng chờ", giới hạn vé/người), **Câu hỏi thường gặp &
  chính sách** (accordion, nội dung tĩnh dùng chung).
- **Mobile:** một cột; `StickyActionBar` (đã có từ 02) hiển thị giá từ + CTA.
- `generateMetadata` + `opengraph-image.tsx` (poster tự sinh).
- 404 khi slug không tồn tại, hoặc là Draft/Cancelled… (xem mục 5).

### CTA state (client)

Hàm thuần `computeCtaState(event, phases, serverNow, availability)` → một trong 6 state của spec, kèm mốc thời gian
kế tiếp để tự chuyển state. Đồng hồ dùng `serverNow` + offset (đã có `lib/server-time.ts` từ 02), nên lệch đồng hồ máy
5 phút vẫn đúng. Một `setTimeout` tới mốc chuyển state, không poll.

## 5. Quyết định kỹ thuật (của mình, bạn có thể sửa)

1. **Chi tiết sự kiện Cancelled → 404** (theo acceptance criteria). **Ended → vẫn hiện** với CTA "Đã kết thúc", vì link
   cũ trên mạng xã hội không nên chết. Danh sách chỉ có Published chưa kết thúc.
2. **Search:** cột `search_vector` là `GENERATED ALWAYS AS … STORED` trên `events`, gồm `unaccent(title)`, lineup và
   **city được denormalize vào `events.city`**. Generated column không đọc được bảng khác. `city` được copy từ venue
   khi tạo event, nên index `(city, starts_at)` cũng nằm trên `events`. `unaccent` không phải IMMUTABLE, nên bọc trong
   hàm `immutable_unaccent` (cách làm chuẩn). Truy vấn: `search_vector @@ websearch_to_tsquery('simple',
   immutable_unaccent(q))`, OR với trigram `immutable_unaccent(title) % immutable_unaccent(q)` để bắt lỗi gõ.
3. **Keyset cursor:** base64url của `{startsAt, id}`; trang 20 mục; `nextCursor` null khi hết.
4. **`priceFrom`** = giá tier thấp nhất, có trong DTO danh sách (card cần nó, spec 02 đã có `Price from`).
5. **HybridCache decorator** trên handler đọc (Scrutor-free: đăng ký thủ công trong DI). Test 1.000 request đồng thời
   ⇒ đúng 1 lần query DB, đếm bằng EF `DbCommandInterceptor` trong integration test.
6. **Seat map ETag** = hash của layout (tính từ dữ liệu ghế), `If-None-Match` ⇒ 304.
7. **`/availability`** trả tất cả còn trống, `remaining` = capacity (GA) hoặc số ghế (Seated), cache 1 s in-memory.
8. **Revalidate hook** `POST /api/revalidate` (route handler Next, so sánh secret constant-time) có sẵn và có test.
   Phía .NET chỉ thêm interface `ICatalogCacheInvalidator`; spec 05 mới gọi nó khi sửa sự kiện.
9. **`/categories` và `/cities`:** danh mục là danh sách tĩnh trong code (slug + tên tiếng Việt). Thành phố là
   distinct từ venues, cache.
10. **Dữ liệu mẫu mở rộng:** từ 6 lên khoảng 14 sự kiện ở 3 thành phố (HCM, Hà Nội, Đà Nẵng) và 6 danh mục, có tiers.
    Hai sự kiện có seat map sinh tự động (vài section × hàng × ghế), để `/seat-map` có dữ liệu thật.

## 6. Kiểm thử

- **Domain:** EventSeat invariants, city denormalize.
- **Integration (Testcontainers):** search "son tung" → "Sơn Tùng M-TP Live"; lỗi gõ; Draft/Cancelled không xuất hiện
  và trả 404; keyset không trùng/không sót; 304 seat map; 1.000 request ⇒ 1 query.
- **Vitest:** `computeCtaState` (gồm lệch đồng hồ ±5 phút và chuyển state tại T-30 phút bằng fake timers), parse/serialize
  bộ lọc URL, `.ics`, poster.
- **Playwright + axe:** trang chủ, danh sách (lọc, xem thêm), chi tiết; mobile sticky CTA.
- **Lighthouse:** thêm `/events` và `/events/son-tung-mtp-live` vào budget (theo phạm vi spec 02: chặn JS budget trên
  trang public).
