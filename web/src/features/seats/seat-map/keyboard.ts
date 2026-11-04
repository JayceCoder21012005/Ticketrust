import type { SeatInfo } from "@/lib/seats/types";

export type SeatKey = "ArrowLeft" | "ArrowRight" | "ArrowUp" | "ArrowDown" | "Home" | "End";

export const SEAT_KEYS = new Set<string>(["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End"]);

/**
 * Seat to focus after a key press, inside the seats on screen (one section). ←/→ stay in the row and stop at its ends;
 * ↑/↓ go to the nearest seat of the nearest row above or below; Home/End jump to the row's ends. Null = stay put.
 */
export function nextSeat(seats: SeatInfo[], current: number, key: SeatKey): number | null {
  const from = seats.find((s) => s.index === current);
  if (!from) {
    return null;
  }

  const row = seats.filter((s) => s.section === from.section && s.row === from.row).sort((a, b) => a.x - b.x);
  switch (key) {
    case "ArrowLeft":
      return [...row].reverse().find((s) => s.x < from.x)?.index ?? null;
    case "ArrowRight":
      return row.find((s) => s.x > from.x)?.index ?? null;
    case "Home":
      return row[0]?.index ?? null;
    case "End":
      return row.at(-1)?.index ?? null;
    default:
      return vertical(seats, from, key === "ArrowUp" ? -1 : 1);
  }
}

function vertical(seats: SeatInfo[], from: SeatInfo, direction: -1 | 1): number | null {
  const candidates = seats.filter((s) => s.section === from.section && s.row !== from.row && Math.sign(s.y - from.y) === direction);
  if (candidates.length === 0) {
    return null;
  }

  const nearestY = Math.min(...candidates.map((s) => Math.abs(s.y - from.y)));
  const row = candidates.filter((s) => Math.abs(Math.abs(s.y - from.y) - nearestY) < 1e-6);
  return row.reduce((best, s) => (Math.abs(s.x - from.x) < Math.abs(best.x - from.x) ? s : best)).index;
}
