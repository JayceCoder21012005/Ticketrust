/** "Thêm vào lịch" (spec 04): a single-event iCalendar file (RFC 5545) that every calendar app imports. */
export type IcsEvent = {
  id: string;
  title: string;
  startsAt: string;
  endsAt: string;
  venueName: string;
  venueAddress: string;
  city: string;
  url: string;
};

const MAX_LINE_OCTETS = 75;
const encoder = new TextEncoder();

function utc(value: string | Date): string {
  return new Date(value).toISOString().replace(/[-:]/g, "").replace(/\.\d{3}/, "");
}

function escapeText(value: string): string {
  return value.replace(/\\/g, "\\\\").replace(/;/g, "\\;").replace(/,/g, "\\,").replace(/\r?\n/g, "\\n");
}

/** Splits at 75 octets without cutting a UTF-8 character; continuation lines start with a space. */
function fold(line: string): string {
  const parts: string[] = [];
  let current = "";
  let octets = 0;

  for (const char of line) {
    const size = encoder.encode(char).length;
    const limit = parts.length === 0 ? MAX_LINE_OCTETS : MAX_LINE_OCTETS - 1;
    if (octets + size > limit) {
      parts.push(current);
      current = "";
      octets = 0;
    }

    current += char;
    octets += size;
  }

  parts.push(current);
  return parts.join("\r\n ");
}

export function buildIcs(event: IcsEvent, now: Date = new Date()): string {
  const location = [event.venueName, event.venueAddress, event.city].filter(Boolean).join(", ");
  const lines = [
    "BEGIN:VCALENDAR",
    "VERSION:2.0",
    "PRODID:-//TicketRush//Catalog//VI",
    "CALSCALE:GREGORIAN",
    "METHOD:PUBLISH",
    "BEGIN:VEVENT",
    `UID:${event.id}@ticketrush`,
    `DTSTAMP:${utc(now)}`,
    `DTSTART:${utc(event.startsAt)}`,
    `DTEND:${utc(event.endsAt)}`,
    `SUMMARY:${escapeText(event.title)}`,
    `LOCATION:${escapeText(location)}`,
    `URL:${event.url}`,
    `DESCRIPTION:${escapeText(event.url)}`,
    "END:VEVENT",
    "END:VCALENDAR",
  ];

  return lines.map(fold).join("\r\n") + "\r\n";
}
