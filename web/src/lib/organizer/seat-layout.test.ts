import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import type { SeatLayoutDefinition, TicketKind } from "@/features/organizer/types";
import { generateSeats, rowLabel, validateLayout } from "./seat-layout";

type Fixture = {
  tiers: Record<string, TicketKind>;
  cases: {
    name: string;
    definition: SeatLayoutDefinition;
    expected: { count: number | null; seats: { index: number; section: string; row: string; number: string; x: number; y: number; tierId: string }[]; errors: { section: string; field: string }[] };
  }[];
};

// The same file the C# generator tests read: both ports must agree seat for seat.
const fixture = JSON.parse(readFileSync(resolve(process.cwd(), "../shared/fixtures/seat-layout.json"), "utf8")) as Fixture;

describe("seat layout (TypeScript port)", () => {
  it.each(fixture.cases.map((c) => [c.name, c] as const))("matches the shared fixture: %s", (_name, c) => {
    const errors = validateLayout(c.definition, fixture.tiers);

    expect(errors.map((e) => `${e.section}.${e.field}`).sort()).toEqual(c.expected.errors.map((e) => `${e.section}.${e.field}`).sort());
    if (c.expected.count === null) return;

    const seats = generateSeats(c.definition);
    expect(seats).toHaveLength(c.expected.count);
    for (const seat of c.expected.seats) {
      expect(seats[seat.index]).toEqual(seat);
    }
  });

  it.each([
    [0, "A"],
    [25, "Z"],
    [26, "AA"],
    [27, "AB"],
    [51, "AZ"],
    [52, "BA"],
  ])("labels row %i as %s", (index, label) => {
    expect(rowLabel(index)).toBe(label);
  });

  it("treats a section without a kind as seated, like the api", () => {
    const seats = generateSeats({ sections: [{ code: "A", tierId: "t", rows: 1, seatsPerRow: 2, origin: { x: 0, y: 0 }, curve: 0 } as never] });

    expect(seats).toHaveLength(2);
  });
});
