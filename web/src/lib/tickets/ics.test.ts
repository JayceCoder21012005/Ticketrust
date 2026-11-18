import { describe, expect, it } from "vitest";
import { buildIcs, escapeIcsText, foldIcsLine, icsFileName } from "./ics";

const utf8Length = (text: string) => new TextEncoder().encode(text).length;

/** Undoes RFC 5545 folding: a CRLF followed by one space joins the lines. */
const unfold = (ics: string) => ics.replace(/\r\n /g, "");

const input = {
  ticketId: "0192f0e0-aaaa-7bbb-8ccc-0123456789ab",
  title: "Đêm nhạc Giao hưởng; Mùa Thu, Hà Nội \\ Đặc biệt",
  startsAt: "2099-12-20T13:00:00+07:00",
  endsAt: "2099-12-20T16:30:00Z",
  location: "Nhà hát Hòa Bình, TP.HCM",
  description: "Hạng VIP · Khu A, hàng 1, ghế 12\nMở vé trên TicketRush",
  url: "https://ticketrush.example/tickets/0192f0e0-aaaa-7bbb-8ccc-0123456789ab",
  now: new Date("2026-11-12T05:06:07.890Z"),
};

describe("ics", () => {
  it("Add_to_calendar_builds_a_valid_ics", () => {
    const ics = buildIcs(input);

    // CRLF everywhere, ending with one, and never a bare LF.
    expect(ics.endsWith("\r\n")).toBe(true);
    expect(ics.replace(/\r\n/g, "")).not.toMatch(/[\r\n]/);
    // Every physical line is at most 75 octets (UTF-8), continuation lines start with one space.
    const physical = ics.split("\r\n").slice(0, -1);
    for (const line of physical) expect(utf8Length(line)).toBeLessThanOrEqual(75);

    const lines = unfold(ics).split("\r\n").slice(0, -1);
    expect(lines[0]).toBe("BEGIN:VCALENDAR");
    expect(lines.at(-1)).toBe("END:VCALENDAR");
    expect(lines).toContain("VERSION:2.0");
    expect(lines.some((line) => line.startsWith("PRODID:"))).toBe(true);
    expect(lines).toContain("BEGIN:VEVENT");
    expect(lines).toContain("END:VEVENT");
    expect(lines).toContain(`UID:ticket-${input.ticketId}@ticketrush`);
    expect(lines).toContain("DTSTAMP:20261112T050607Z");
    // Times in UTC, whatever offset the api used.
    expect(lines).toContain("DTSTART:20991220T060000Z");
    expect(lines).toContain("DTEND:20991220T163000Z");
    expect(lines).toContain("SUMMARY:Đêm nhạc Giao hưởng\\; Mùa Thu\\, Hà Nội \\\\ Đặc biệt");
    expect(lines).toContain("LOCATION:Nhà hát Hòa Bình\\, TP.HCM");
    expect(lines).toContain("DESCRIPTION:Hạng VIP · Khu A\\, hàng 1\\, ghế 12\\nMở vé trên TicketRush");
    expect(lines).toContain(`URL:${input.url}`);
  });

  it("escapes backslash, semicolon, comma and newlines (RFC 5545 3.3.11)", () => {
    expect(escapeIcsText("a\\b;c,d\r\ne\nf")).toBe("a\\\\b\\;c\\,d\\ne\\nf");
  });

  it("folds at 75 octets without splitting a multi-byte character", () => {
    const line = `SUMMARY:${"ệ".repeat(60)}`;
    const folded = foldIcsLine(line);
    const parts = folded.split("\r\n");

    expect(parts.length).toBeGreaterThan(1);
    for (const part of parts) expect(utf8Length(part)).toBeLessThanOrEqual(75);
    for (const part of parts.slice(1)) expect(part.startsWith(" ")).toBe(true);
    expect(folded.replace(/\r\n /g, "")).toBe(line);
    // No replacement characters: every chunk is valid UTF-8 on its own.
    for (const part of parts) expect(new TextDecoder("utf-8", { fatal: true }).decode(new TextEncoder().encode(part))).toBe(part);
  });

  it("leaves a short line alone", () => {
    expect(foldIcsLine("VERSION:2.0")).toBe("VERSION:2.0");
    expect(foldIcsLine("X".repeat(75))).toBe("X".repeat(75));
    expect(foldIcsLine("X".repeat(76))).toBe(`${"X".repeat(75)}\r\n X`);
  });

  it("names the file after the event", () => {
    expect(icsFileName("dem-nhac-1")).toBe("dem-nhac-1.ics");
    expect(icsFileName("")).toBe("ticketrush.ics");
  });
});
