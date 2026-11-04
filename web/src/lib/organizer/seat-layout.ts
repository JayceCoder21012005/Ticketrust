import type { SeatLayoutDefinition, SectionDefinition, TicketKind } from "@/features/organizer/types";

/**
 * TypeScript port of the api's `SeatLayoutGenerator` for the live preview (spec 05). The server stays the source of
 * truth; `shared/fixtures/seat-layout.json` keeps both ports identical, so change them together.
 */
export const CANVAS_WIDTH = 1000;
export const CANVAS_HEIGHT = 700;
export const SEAT_PITCH = 24;
export const ROW_PITCH = 28;
export const MAX_CURVE = 0.5;
export const MAX_ROWS = 100;
export const MAX_SEATS_PER_ROW = 100;
export const MAX_SEATS = 20_000;
export const MAX_SECTIONS = 50;

export type GeneratedSeat = { index: number; section: string; row: string; number: string; x: number; y: number; tierId: string };
export type LayoutError = { section: string; field: string; message: string };

const CODE = /^[A-Za-z0-9]{1,8}$/;

const kindOf = (section: SectionDefinition) => section.kind ?? "seated";

const isIntIn = (value: number | null | undefined, min: number, max: number): value is number =>
  typeof value === "number" && Number.isInteger(value) && value >= min && value <= max;

export function validateLayout(definition: SeatLayoutDefinition, tiers: Record<string, TicketKind>): LayoutError[] {
  const errors: LayoutError[] = [];
  if (definition.sections.length === 0) {
    return [{ section: "", field: "sections", message: "Thêm ít nhất một khu." }];
  }

  // Standing sections have no seats, so the seat cap alone doesn't bound the stored layout.
  if (definition.sections.length > MAX_SECTIONS) {
    return [{ section: "", field: "sections", message: `Tối đa ${MAX_SECTIONS} khu mỗi sơ đồ.` }];
  }

  const seen = new Set<string>();
  let total = 0;
  for (const section of definition.sections) {
    const code = section.code ?? "";
    const fail = (field: string, message: string) => errors.push({ section: code, field, message });

    if (!CODE.test(code)) {
      fail("code", "Mã khu gồm 1–8 chữ hoặc số.");
    } else if (seen.has(code.toUpperCase())) {
      fail("code", "Mã khu bị trùng.");
    } else {
      seen.add(code.toUpperCase());
    }

    const standing = kindOf(section) === "standing";
    if (tiers[section.tierId] !== (standing ? "GeneralAdmission" : "Seated")) {
      fail("tierId", standing ? "Khu đứng cần một hạng vé loại Đứng." : "Khu ngồi cần một hạng vé loại Ngồi.");
    }

    if (standing) {
      continue;
    }

    const shapeErrorsBefore = errors.length;
    if (!isIntIn(section.rows, 1, MAX_ROWS)) fail("rows", `Số hàng từ 1 đến ${MAX_ROWS}.`);
    if (!isIntIn(section.seatsPerRow, 1, MAX_SEATS_PER_ROW)) fail("seatsPerRow", `Số ghế mỗi hàng từ 1 đến ${MAX_SEATS_PER_ROW}.`);
    if (!(section.curve >= 0 && section.curve <= MAX_CURVE)) fail("curve", `Độ cong từ 0 đến ${MAX_CURVE}.`);

    const origin = section.origin;
    if (!origin || !(origin.x >= 0 && origin.x <= CANVAS_WIDTH && origin.y >= 0 && origin.y <= CANVAS_HEIGHT)) {
      fail("origin", `Vị trí phải nằm trong khung ${CANVAS_WIDTH}×${CANVAS_HEIGHT}.`);
    }

    if ((section.rows ?? 0) > 0 && (section.seatsPerRow ?? 0) > 0) {
      total += section.rows! * section.seatsPerRow!;
    }

    if (errors.length === shapeErrorsBefore) {
      // The origin is the top-left seat; the far corner must stay on the canvas too, or the preview hides seats.
      const width = (section.seatsPerRow! - 1) * SEAT_PITCH;
      if (origin!.x + width > CANVAS_WIDTH) fail("seatsPerRow", "Khu vượt quá mép phải khung: giảm số ghế mỗi hàng hoặc dời sang trái.");
      if (origin!.y + (section.rows! - 1) * ROW_PITCH + section.curve * width > CANVAS_HEIGHT) {
        fail("rows", "Khu vượt quá mép dưới khung: giảm số hàng, độ cong hoặc dời lên trên.");
      }
    }
  }

  if (total > MAX_SEATS) {
    errors.push({ section: "", field: "sections", message: `Tối đa ${MAX_SEATS.toLocaleString("vi-VN")} ghế mỗi sự kiện.` });
  }

  return errors;
}

// Same arithmetic as the C# port: floor(v·100 + 0.5) / 100 (inputs are ≥ 0).
const round = (value: number) => Math.floor(value * 100 + 0.5) / 100;

/** Seats in (section order, row, number) order with contiguous indexes from 0. Assumes a valid definition. */
export function generateSeats(definition: SeatLayoutDefinition): GeneratedSeat[] {
  const seats: GeneratedSeat[] = [];
  for (const section of definition.sections) {
    if (kindOf(section) !== "seated") continue;

    const rows = section.rows!;
    const perRow = section.seatsPerRow!;
    const origin = section.origin!;
    const width = (perRow - 1) * SEAT_PITCH;

    for (let r = 0; r < rows; r++) {
      const row = rowLabel(r);
      for (let n = 0; n < perRow; n++) {
        const t = perRow === 1 ? 0.5 : n / (perRow - 1);
        const sag = section.curve * width * (1 - (2 * t - 1) ** 2);
        seats.push({
          index: seats.length,
          section: section.code,
          row,
          number: String(n + 1),
          x: round(origin.x + n * SEAT_PITCH),
          y: round(origin.y + r * ROW_PITCH + sag),
          tierId: section.tierId,
        });
      }
    }
  }

  return seats;
}

/** 0 → A, 25 → Z, 26 → AA (spreadsheet-style). */
export function rowLabel(zeroBased: number): string {
  let label = "";
  for (let n = zeroBased + 1; n > 0; n = Math.floor((n - 1) / 26)) {
    label = String.fromCharCode(65 + ((n - 1) % 26)) + label;
  }

  return label;
}
