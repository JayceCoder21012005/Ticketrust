import { formatVnd } from "@/lib/format";
import type { SeatInfo, SeatState } from "./types";

const STATE: Record<SeatState, string> = {
  free: "còn trống",
  taken: "đã có người chọn",
  selected: "đang chọn",
  mine: "bạn đang giữ",
};

/** Screen-reader name of a seat (spec 07): `Khu A, hàng 3, ghế 12, 1.500.000 ₫, còn trống`. */
export function seatLabel(seat: SeatInfo, price: number, state: SeatState): string {
  return `Khu ${seat.section}, hàng ${seat.row}, ghế ${seat.number}, ${formatVnd(price)}, ${STATE[state]}`;
}
