import type { SeatInfo } from "@/lib/seats/types";
import { SEAT_RADIUS } from "./seat";

export type Box = { x: number; y: number; width: number; height: number };

export type OverviewLayout = {
  /** Frames the stage and every section, with a margin: the overview follows the venue, not a fixed canvas. */
  viewBox: Box;
  stage: Box;
  sections: { name: string; box: Box }[];
};

/** Room around a section's seats, so its outline does not touch the outer seats. */
const SECTION_INSET = 6;
const MARGIN = 24;

export function boundsOf(seats: SeatInfo[]): Box {
  const xs = seats.map((s) => s.x);
  const ys = seats.map((s) => s.y);
  const x = Math.min(...xs) - SEAT_RADIUS;
  const y = Math.min(...ys) - SEAT_RADIUS;
  return { x, y, width: Math.max(...xs) + SEAT_RADIUS - x, height: Math.max(...ys) + SEAT_RADIUS - y };
}

function union(boxes: Box[]): Box {
  const x = Math.min(...boxes.map((b) => b.x));
  const y = Math.min(...boxes.map((b) => b.y));
  return { x, y, width: Math.max(...boxes.map((b) => b.x + b.width)) - x, height: Math.max(...boxes.map((b) => b.y + b.height)) - y };
}

/**
 * Where the overview draws each section and the stage. Seat coordinates come from the organizer's layout (or the seed) and can
 * be anywhere, and the layout has no stage position: the stage goes centred above all sections (the convention of every seat
 * map we draw: rows count up away from it), sized to the seating, and the frame is fitted to the result.
 */
export function overviewLayout(seats: SeatInfo[], sectionNames: string[]): OverviewLayout {
  if (seats.length === 0 || sectionNames.length === 0) {
    const stage = { x: 350, y: 20, width: 300, height: 44 };
    return { viewBox: { x: 0, y: 0, width: 1000, height: 700 }, stage, sections: [] };
  }

  const sections = sectionNames.map((name) => {
    const own = boundsOf(seats.filter((s) => s.section === name));
    return {
      name,
      box: { x: own.x - SECTION_INSET, y: own.y - SECTION_INSET, width: own.width + SECTION_INSET * 2, height: own.height + SECTION_INSET * 2 },
    };
  });
  const seating = union(sections.map((s) => s.box));

  // Readable at any venue size: about half the seating's width, a band whose height scales with it, within sensible limits.
  const width = seating.width * 0.5;
  const height = Math.min(Math.max(seating.width * 0.06, 24), 60);
  const gap = Math.max(height * 0.6, 16);
  const stage = { x: seating.x + (seating.width - width) / 2, y: seating.y - gap - height, width, height };

  const content = union([stage, seating]);
  return {
    viewBox: { x: content.x - MARGIN, y: content.y - MARGIN, width: content.width + MARGIN * 2, height: content.height + MARGIN * 2 },
    stage,
    sections,
  };
}
