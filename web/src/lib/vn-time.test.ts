import { describe, expect, it } from "vitest";
import { formatVietnam, fromIsoToVietnam, toVietnamIso } from "./vn-time";

// `npm run test:tz` runs this file again with TZ=America/New_York: the results must not depend on the device zone.
describe("Vietnam wall time", () => {
  it("toVietnamIso keeps the entered wall time regardless of TZ", () => {
    expect(toVietnamIso("2026-12-01", "00:30")).toBe("2026-12-01T00:30:00+07:00");
    expect(toVietnamIso("2026-12-31", "23:59")).toBe("2026-12-31T23:59:00+07:00");
  });

  it("reads an instant back as the Vietnam date and time it was entered as", () => {
    expect(fromIsoToVietnam("2026-11-30T17:30:00Z")).toEqual({ date: "2026-12-01", time: "00:30" });
    expect(fromIsoToVietnam(toVietnamIso("2027-01-01", "07:05"))).toEqual({ date: "2027-01-01", time: "07:05" });
  });

  it("formats with the time, weekday, date and zone", () => {
    expect(formatVietnam("2026-12-06T12:30:00Z")).toBe("19:30, CN 06/12/2026 (GMT+7)");
    expect(formatVietnam("2026-12-05T17:00:00Z")).toBe("00:00, CN 06/12/2026 (GMT+7)");
  });

  it("rejects malformed input instead of sending a wrong instant", () => {
    expect(() => toVietnamIso("2026-13-01", "10:00")).toThrow();
    expect(() => toVietnamIso("2026-12-01", "24:00")).toThrow();
  });
});
