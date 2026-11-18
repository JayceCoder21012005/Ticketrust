import { ApiError, NetworkError } from "@/lib/errors";
import { fromIsoToVietnam } from "@/lib/vn-time";
import type { ScanResponse } from "./api";

/** green: let in; amber: already in; red: do not let in. */
export type Tone = "ok" | "warn" | "bad";

export type ScanView = {
  tone: Tone;
  title: string;
  /** Seat in huge type (admitted), or the reason (refused). */
  headline?: string;
  /** Smaller lines under the headline. */
  lines: string[];
};

const REFUSALS: Record<string, string> = {
  Invalid: "Mã không hợp lệ",
  Expired: "Mã đã hết hạn — nhờ khách mở lại vé",
  WrongEvent: "Vé của sự kiện khác",
  NotValid: "Vé không còn hiệu lực",
};

const REASONS: Record<string, string> = {
  voided: "Vé đã bị huỷ",
  listed: "Vé đang được rao bán lại",
  bad_format: "Không phải mã vé",
  bad_signature: "Chữ ký không đúng",
  unknown_ticket: "Không tìm thấy vé",
};

/** "Khu A · Hàng 3 · Ghế 12", or the tier alone for general admission. */
export function seatLabel(ticket: NonNullable<ScanResponse["ticket"]>): string {
  if (!ticket.section) return ticket.tier;
  return `Khu ${ticket.section} · Hàng ${ticket.row} · Ghế ${ticket.seat}`;
}

function clock(iso: string | null): string | null {
  return iso ? fromIsoToVietnam(iso).time : null;
}

/** What the gate screen shows for an answer of the scan endpoint. */
export function describeScan(response: ScanResponse): ScanView {
  const { ticket } = response;
  if (response.result === "Admitted") {
    return { tone: "ok", title: "Hợp lệ", headline: ticket ? seatLabel(ticket) : undefined, lines: ticket ? [ticket.holderName, ticket.tier] : [] };
  }

  if (response.result === "AlreadyCheckedIn") {
    const time = clock(response.checkedInAt);
    const where = response.gateName ? ` – ${response.gateName}` : "";
    return {
      tone: "warn",
      title: time ? `Đã check-in lúc ${time}${where}` : `Đã check-in${where}`,
      headline: ticket ? seatLabel(ticket) : undefined,
      lines: ticket ? [ticket.holderName] : [],
    };
  }

  const reason = response.reason ? REASONS[response.reason] : undefined;
  return { tone: "bad", title: REFUSALS[response.result] ?? "Không hợp lệ", lines: reason ? [reason] : [] };
}

/** A failed request, as something to tell the staff (the screen is red: the ticket was NOT checked). */
export function describeFailure(error: unknown): ScanView {
  if (error instanceof ApiError && error.status === 429) {
    return { tone: "bad", title: "Quét quá nhanh", lines: ["Chờ một giây rồi quét lại"] };
  }

  if (error instanceof ApiError && error.status === 404) {
    return { tone: "bad", title: "Bạn không được phân công cho sự kiện này", lines: [] };
  }

  if (error instanceof NetworkError) {
    return { tone: "bad", title: "Mất kết nối", lines: ["Vé CHƯA được kiểm tra — thử lại khi có mạng"] };
  }

  return { tone: "bad", title: "Hệ thống đang bận", lines: ["Vé CHƯA được kiểm tra — thử lại"] };
}
