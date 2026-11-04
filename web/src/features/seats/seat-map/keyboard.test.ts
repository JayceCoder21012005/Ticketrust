import { describe, expect, it } from "vitest";
import type { SeatInfo } from "@/lib/seats/types";
import { nextSeat } from "./keyboard";

// Section A: 3 rows × 4 seats, index = row * 4 + seat; x grows to the right, y grows downwards.
const seats: SeatInfo[] = Array.from({ length: 12 }, (_, i) => ({
  index: i,
  section: "A",
  row: String(Math.floor(i / 4) + 1),
  number: String((i % 4) + 1),
  x: 100 + (i % 4) * 24,
  y: 100 + Math.floor(i / 4) * 28,
  tierId: "t",
}));

describe("nextSeat", () => {
  it("moves along the row", () => {
    expect(nextSeat(seats, 5, "ArrowRight")).toBe(6);
    expect(nextSeat(seats, 5, "ArrowLeft")).toBe(4);
  });

  it("stops at the ends of a row instead of wrapping", () => {
    expect(nextSeat(seats, 7, "ArrowRight")).toBeNull();
    expect(nextSeat(seats, 4, "ArrowLeft")).toBeNull();
  });

  it("moves to the nearest seat of the next row", () => {
    expect(nextSeat(seats, 5, "ArrowDown")).toBe(9);
    expect(nextSeat(seats, 5, "ArrowUp")).toBe(1);
  });

  it("stops at the first and last row", () => {
    expect(nextSeat(seats, 1, "ArrowUp")).toBeNull();
    expect(nextSeat(seats, 10, "ArrowDown")).toBeNull();
  });

  it("jumps to the start and end of the row with Home and End", () => {
    expect(nextSeat(seats, 6, "Home")).toBe(4);
    expect(nextSeat(seats, 6, "End")).toBe(7);
  });

  it("returns null for an unknown seat", () => {
    expect(nextSeat(seats, 99, "ArrowRight")).toBeNull();
  });
});
