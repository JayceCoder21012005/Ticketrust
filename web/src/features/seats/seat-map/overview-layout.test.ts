import { describe, expect, it } from "vitest";
import type { SeatInfo } from "@/lib/seats/types";
import { overviewLayout, type Box } from "./overview-layout";

/** The seeded concert: three sections of 10 × 20 seats side by side, starting 12 units from the top. */
function seededSeats(): SeatInfo[] {
  const seats: SeatInfo[] = [];
  ["A", "B", "C"].forEach((section, s) => {
    for (let row = 1; row <= 10; row++) {
      for (let number = 1; number <= 20; number++) {
        seats.push({ index: seats.length, section, row: String(row), number: String(number), x: (s * 22 + number) * 10, y: row * 12, tierId: "t" });
      }
    }
  });
  return seats;
}

const overlaps = (a: Box, b: Box) => a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;
const contains = (outer: Box, inner: Box) =>
  inner.x >= outer.x && inner.y >= outer.y && inner.x + inner.width <= outer.x + outer.width && inner.y + inner.height <= outer.y + outer.height;

describe("overviewLayout", () => {
  it("puts the stage above every section, never on top of one", () => {
    const layout = overviewLayout(seededSeats(), ["A", "B", "C"]);

    for (const section of layout.sections) {
      expect(overlaps(layout.stage, section.box)).toBe(false);
      expect(layout.stage.y + layout.stage.height).toBeLessThan(section.box.y);
    }
  });

  it("centres the stage over the seats and sizes it to them", () => {
    const layout = overviewLayout(seededSeats(), ["A", "B", "C"]);
    const left = Math.min(...layout.sections.map((s) => s.box.x));
    const right = Math.max(...layout.sections.map((s) => s.box.x + s.box.width));

    expect(layout.stage.x + layout.stage.width / 2).toBeCloseTo((left + right) / 2, 5);
    expect(layout.stage.width).toBeGreaterThan((right - left) * 0.3);
    expect(layout.stage.width).toBeLessThan(right - left);
  });

  it("frames the stage and the sections tightly instead of a fixed canvas", () => {
    const layout = overviewLayout(seededSeats(), ["A", "B", "C"]);

    expect(contains(layout.viewBox, layout.stage)).toBe(true);
    for (const section of layout.sections) expect(contains(layout.viewBox, section.box)).toBe(true);
    // The seats span ~650 × 110 units: the frame follows them, not a 1000 × 700 canvas.
    expect(layout.viewBox.width).toBeLessThan(800);
    expect(layout.viewBox.height).toBeLessThan(320);
  });

  it("works wherever an organizer drew the seats", () => {
    const far = seededSeats().map((s) => ({ ...s, x: s.x + 5000, y: s.y + 3000 }));
    const layout = overviewLayout(far, ["A", "B", "C"]);

    expect(layout.viewBox.x).toBeGreaterThan(4000);
    for (const section of layout.sections) expect(overlaps(layout.stage, section.box)).toBe(false);
  });
});
