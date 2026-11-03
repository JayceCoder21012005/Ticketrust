import { describe, expect, it } from "vitest";
import { formatDateTime, formatEventDate, formatTime, formatVnd } from "./format";

// Intl uses non-breaking spaces; compare on normal spaces for readability.
const plain = (s: string) => s.replace(/\u00a0|\u202f/g, " ");

describe("formatVnd", () => {
  it("formats whole dong with Vietnamese grouping and the ₫ sign", () => {
    expect(plain(formatVnd(800_000))).toBe("800.000 ₫");
  });

  it("formats zero", () => {
    expect(plain(formatVnd(0))).toBe("0 ₫");
  });
});

describe("dates are shown in Asia/Ho_Chi_Minh regardless of the device time zone", () => {
  // 2026-12-31T17:30Z is 00:30 on 1 Jan 2027 in Vietnam (UTC+7).
  const instant = "2026-12-31T17:30:00Z";

  it("formatTime", () => {
    expect(formatTime(instant)).toBe("00:30");
  });

  it("formatDateTime", () => {
    expect(plain(formatDateTime(instant))).toBe("00:30 01/01/2027");
  });

  it("formatEventDate shows weekday, day, month and year", () => {
    expect(plain(formatEventDate(instant))).toBe("Thứ Sáu, 1 tháng 1, 2027");
  });
});
