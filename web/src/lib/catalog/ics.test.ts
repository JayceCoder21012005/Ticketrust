import { describe, expect, it } from "vitest";
import { buildIcs } from "./ics";

const event = {
  id: "0193a1b2-0000-7000-8000-000000000001",
  title: "Sơn Tùng M-TP Live; Đêm, nhạc",
  startsAt: "2026-12-01T12:30:00Z",
  endsAt: "2026-12-01T15:30:00Z",
  venueName: "Sân vận động Mỹ Đình",
  venueAddress: "Lê Đức Thọ, Nam Từ Liêm",
  city: "Hà Nội",
  url: "https://ticketrush.vn/events/son-tung-mtp-live",
};

describe("buildIcs", () => {
  const ics = buildIcs(event, new Date("2026-11-03T00:00:00Z"));
  // Readers unfold "CRLF + space" continuations before parsing; so do the assertions.
  const lines = ics.replace(/\r\n /g, "").split("\r\n");

  it("is a VCALENDAR with one VEVENT and CRLF line endings", () => {
    expect(lines[0]).toBe("BEGIN:VCALENDAR");
    expect(lines.at(-2)).toBe("END:VCALENDAR");
    expect(lines.at(-1)).toBe("");
    expect(lines.filter((l) => l === "BEGIN:VEVENT")).toHaveLength(1);
  });

  it("uses UTC times and a stable UID", () => {
    expect(lines).toContain("DTSTART:20261201T123000Z");
    expect(lines).toContain("DTEND:20261201T153000Z");
    expect(lines).toContain("DTSTAMP:20261103T000000Z");
    expect(lines).toContain("UID:0193a1b2-0000-7000-8000-000000000001@ticketrush");
  });

  it("escapes commas and semicolons in text", () => {
    expect(lines).toContain("SUMMARY:Sơn Tùng M-TP Live\\; Đêm\\, nhạc");
    expect(lines).toContain("LOCATION:Sân vận động Mỹ Đình\\, Lê Đức Thọ\\, Nam Từ Liêm\\, Hà Nội");
  });

  it("folds lines longer than 75 octets", () => {
    const long = buildIcs({ ...event, title: "Ă".repeat(60) }, new Date("2026-11-03T00:00:00Z"));

    for (const line of long.split("\r\n")) {
      expect(new TextEncoder().encode(line).length).toBeLessThanOrEqual(75);
    }
    expect(long).toContain("\r\n ");
  });
});
