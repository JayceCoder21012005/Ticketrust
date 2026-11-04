import { describe, expect, it } from "vitest";
import { etaLabel, formatPosition, progressPercent } from "./format";

describe("formatPosition", () => {
  it("uses Vietnamese thousands separators", () => {
    expect(formatPosition(1)).toBe("#1");
    expect(formatPosition(1234)).toBe("#1.234");
    expect(formatPosition(100000)).toBe("#100.000");
  });
});

describe("etaLabel", () => {
  it.each([
    [0, "dưới 1 phút"],
    [45, "dưới 1 phút"],
    [60, "khoảng 1 phút"],
    [240, "khoảng 4 phút"],
    [270, "khoảng 5 phút"],
    [3_000, "khoảng 50 phút"],
    [5_400, "khoảng 2 giờ"],
    [10_800, "khoảng 3 giờ"],
  ])("rounds %i s to %s", (seconds, label) => {
    expect(etaLabel(seconds)).toBe(label);
  });
});

describe("progressPercent", () => {
  it("is 0 at the start, 100 at the front, and never leaves 0-100", () => {
    expect(progressPercent(800, 800)).toBe(0);
    expect(progressPercent(800, 400)).toBe(50);
    expect(progressPercent(800, 1)).toBe(100);
    expect(progressPercent(800, 900)).toBe(0);
    expect(progressPercent(null, 5)).toBe(0);
    expect(progressPercent(1, 1)).toBe(100);
  });
});
