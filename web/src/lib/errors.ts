/** RFC 9457 body returned by the api (spec 00): `code` is the stable machine-readable key. */
export type ProblemDetails = {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  traceId?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
  [extension: string]: unknown;
};

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemDetails,
  ) {
    super(problem.title ?? `Request failed with ${status}`);
    this.name = "ApiError";
  }

  get code(): string | undefined {
    return this.problem.code;
  }
}

/** The one place that turns api error codes into Vietnamese copy. Each spec adds its codes here. */
const messages: Record<string, string> = {
  "system.busy": "Hệ thống đang bận. Vui lòng thử lại sau giây lát.",
  "auth.invalid_credentials": "Email hoặc mật khẩu không đúng.",
  "auth.email_not_confirmed": "Bạn cần xác nhận email trước khi tiếp tục.",
  "auth.locked_out": "Tài khoản tạm khoá do đăng nhập sai nhiều lần. Thử lại sau 15 phút.",
  "auth.refresh_invalid": "Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.",
  "auth.weak_password": "Mật khẩu cần ít nhất 10 ký tự, gồm cả chữ và số.",
  "captcha.failed": "Xác minh không thành công. Vui lòng thử lại.",
  "event.not_found": "Không tìm thấy sự kiện.",
  "event.not_editable": "Sự kiện đã xuất bản nên không thể sửa mục này.",
  "event.phase_overlap": "Các đợt mở bán không được trùng thời gian.",
  "event.publish_checklist_failed": "Sự kiện chưa đủ điều kiện để xuất bản.",
  "layout.invalid": "Sơ đồ chỗ ngồi không hợp lệ.",
  "queue.not_open": "Phòng chờ chưa mở.",
  "queue.closed": "Phòng chờ đã đóng.",
  "queue.invalid_access_code": "Mã truy cập không đúng.",
  "queue.email_not_confirmed": "Bạn cần xác nhận email để vào phòng chờ.",
  "admission.required": "Bạn cần vào phòng chờ trước khi chọn ghế.",
  "admission.invalid": "Lượt mua của bạn đã hết hạn. Vui lòng vào lại phòng chờ.",
  "seat.already_held": "Ghế này vừa có người giữ. Vui lòng chọn ghế khác.",
  "hold.seats_unavailable": "Một số ghế bạn chọn không còn trống.",
  "hold.limit_exceeded": "Bạn đã chọn quá số vé tối đa cho mỗi người.",
  "hold.already_exists": "Bạn đang giữ ghế khác. Hãy hoàn tất hoặc huỷ trước.",
  "hold.empty_selection": "Bạn chưa chọn ghế nào.",
  "hold.insufficient_quantity": "Không còn đủ vé cho số lượng bạn chọn.",
  "hold.invalid_seat": "Ghế không hợp lệ.",
  "hold.not_found": "Lượt giữ ghế không còn tồn tại.",
  "order.expired": "Đơn hàng đã hết hạn thanh toán.",
  "order.hold_expired": "Thời gian giữ ghế đã hết. Vui lòng chọn lại.",
  "order.hold_invalid": "Lượt giữ ghế không hợp lệ.",
  "order.not_payable": "Đơn hàng không thể thanh toán.",
  "payment.gateway_unavailable": "Cổng thanh toán đang gián đoạn. Vui lòng thử lại sau.",
  "idempotency.in_progress": "Yêu cầu đang được xử lý. Vui lòng chờ.",
  "idempotency.key_reused": "Yêu cầu bị trùng. Vui lòng tải lại trang.",
  "ticket.not_found": "Không tìm thấy vé.",
  "ticket.not_displayable": "Vé chưa thể hiển thị lúc này.",
  "waitlist.already_joined": "Bạn đã có trong danh sách chờ.",
  "waitlist.not_available": "Danh sách chờ không khả dụng.",
  "waitlist.offer_expired": "Lời mời mua vé đã hết hạn.",
  "resale.listing_unavailable": "Vé này không còn được bán lại.",
  "resale.not_allowed": "Vé này không được phép bán lại.",
  "resale.own_listing": "Bạn không thể mua vé do chính mình đăng bán.",
  "resale.price_above_cap": "Giá bán lại vượt mức cho phép.",
  "transfer.not_allowed": "Vé này không thể chuyển nhượng.",
  "transfer.self": "Bạn không thể chuyển vé cho chính mình.",
};

const byStatus: Record<number, string> = {
  400: "Dữ liệu chưa hợp lệ. Vui lòng kiểm tra lại.",
  401: "Vui lòng đăng nhập để tiếp tục.",
  403: "Bạn không có quyền thực hiện thao tác này.",
  404: "Không tìm thấy nội dung bạn yêu cầu.",
  409: "Dữ liệu vừa thay đổi. Vui lòng tải lại và thử lại.",
  429: "Bạn thao tác quá nhiều lần. Vui lòng chờ một chút.",
  503: "Hệ thống đang bận. Vui lòng thử lại sau giây lát.",
};

const fallback = "Đã có lỗi xảy ra. Vui lòng thử lại.";
const offline = "Không kết nối được máy chủ. Kiểm tra kết nối mạng và thử lại.";

export function toFriendlyMessage(error: unknown): string {
  if (!(error instanceof ApiError)) {
    return offline;
  }

  return (error.code && messages[error.code]) || byStatus[error.status] || fallback;
}
