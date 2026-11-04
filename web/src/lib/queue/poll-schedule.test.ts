import { describe, expect, it } from "vitest";
import { nextDelayMs } from "./poll-schedule";

describe("nextDelayMs", () => {
  it("follows the server's pollAfterMs", () => {
    expect(nextDelayMs({ pollAfterMs: 4_000 }, 0)).toBe(4_000);
    expect(nextDelayMs({ pollAfterMs: 15_000 }, 0)).toBe(15_000);
  });

  it("never polls faster than 500 ms or sleeps longer than 5 minutes, whatever the server says", () => {
    expect(nextDelayMs({ pollAfterMs: 0 }, 0)).toBe(500);
    expect(nextDelayMs({ pollAfterMs: 10 * 60_000 }, 0)).toBe(300_000);
  });

  it("backs off exponentially on errors and caps at 30 s", () => {
    const middle = () => 0.5; // jitter factor 1.0
    expect(nextDelayMs(null, 1, middle)).toBe(1_000);
    expect(nextDelayMs(null, 2, middle)).toBe(2_000);
    expect(nextDelayMs(null, 3, middle)).toBe(4_000);
    expect(nextDelayMs(null, 10, middle)).toBe(30_000);
  });

  it("jitters error retries by +-20% so a recovering api is not hit in lockstep", () => {
    expect(nextDelayMs(null, 3, () => 0)).toBe(3_200);
    expect(nextDelayMs(null, 3, () => 1)).toBe(4_800);
  });
});
