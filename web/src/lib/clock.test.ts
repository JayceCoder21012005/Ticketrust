import { describe, expect, it } from "vitest";
import { clockOffsetMs, formatCountdown, remainingMs } from "./clock";

describe("clockOffsetMs", () => {
  it("is how far the server clock is ahead of the device clock", () => {
    // Device clock is 5 minutes slow.
    const serverNow = "2026-11-03T10:05:00Z";
    const deviceNowAtReceipt = Date.parse("2026-11-03T10:00:00Z");

    expect(clockOffsetMs(serverNow, deviceNowAtReceipt)).toBe(5 * 60_000);
  });
});

describe("remainingMs", () => {
  it("uses server time, not the (wrong) device time", () => {
    const offset = 5 * 60_000; // device is 5 min slow
    const deviceNow = Date.parse("2026-11-03T10:00:00Z"); // server thinks 10:05
    const expiresAt = "2026-11-03T10:10:00Z";

    expect(remainingMs(expiresAt, deviceNow, offset)).toBe(5 * 60_000);
  });

  it("never goes below zero", () => {
    expect(remainingMs("2026-11-03T10:00:00Z", Date.parse("2026-11-03T11:00:00Z"), 0)).toBe(0);
  });
});

describe("formatCountdown", () => {
  it.each([
    [0, "00:00"],
    [999, "00:01"], // round up: never show 00:00 while time is left
    [65_000, "01:05"],
    [3_600_000 + 61_000, "1:01:01"],
  ])("%i ms → %s", (ms, expected) => {
    expect(formatCountdown(ms)).toBe(expected);
  });
});
