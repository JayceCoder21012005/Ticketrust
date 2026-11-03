import { describe, expect, it } from "vitest";
import { calendarTile, formatDateTime, formatDayMonth, formatEventDate, formatTime, formatTimeRange, formatVnd } from "./format";

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

  it("formatDayMonth is the short 'HH:mm dd/MM' used on badges", () => {
    expect(formatDayMonth(instant)).toBe("00:30 01/01");
  });

  it("formatTimeRange shows the start and end time of one evening", () => {
    expect(plain(formatTimeRange("2026-12-01T12:30:00Z", "2026-12-01T15:30:00Z"))).toBe("19:30 – 22:30");
  });

  it("calendarTile gives the month label and day for the date block", () => {
    expect(calendarTile(instant)).toEqual({ month: "Th1", day: "1" });
    expect(calendarTile("2026-12-15T05:00:00Z")).toEqual({ month: "Th12", day: "15" });
  });
});
