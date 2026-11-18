/**
 * A one-event iCalendar file (RFC 5545) for "Thêm vào lịch", built in the browser: no api call, works offline. Times
 * are written in UTC (`…Z`), so every calendar app shows them in its own zone.
 */
export type IcsEvent = {
  ticketId: string;
  title: string;
  startsAt: string;
  endsAt: string;
  location: string;
  description: string;
  /** The wallet screen of the ticket. */
  url: string;
  /** DTSTAMP: when the file was made. */
  now: Date;
};

const CRLF = "\r\n";
const MAX_OCTETS = 75;

/** TEXT values (3.3.11): backslash, semicolon and comma are escaped, a line break becomes `\n`. */
export function escapeIcsText(value: string): string {
  return value
    .replace(/\\/g, "\\\\")
    .replace(/;/g, "\\;")
    .replace(/,/g, "\\,")
    .replace(/\r\n|\r|\n/g, "\\n");
}

const encoder = new TextEncoder();

/**
 * Folds a content line (3.1): at most 75 octets per physical line, continuations start with one space. Splits between
 * characters, never inside a UTF-8 sequence (Vietnamese letters are 2–3 octets).
 */
export function foldIcsLine(line: string): string {
  const parts: string[] = [];
  let current = "";
  let octets = 0;
  for (const char of line) {
    const size = encoder.encode(char).length;
    // A continuation line spends one of its 75 octets on the leading space.
    const limit = parts.length === 0 ? MAX_OCTETS : MAX_OCTETS - 1;
    if (octets + size > limit) {
      parts.push(current);
      current = "";
      octets = 0;
    }
    current += char;
    octets += size;
  }
  parts.push(current);
  return parts.join(`${CRLF} `);
}

/** `20991220T130000Z` */
function utcStamp(value: string | Date): string {
  return new Date(value).toISOString().replace(/[-:]/g, "").replace(/\.\d{3}/, "");
}

export function buildIcs(event: IcsEvent): string {
  const lines = [
    "BEGIN:VCALENDAR",
    "VERSION:2.0",
    "PRODID:-//TicketRush//Vi ve//VI",
    "CALSCALE:GREGORIAN",
    "METHOD:PUBLISH",
    "BEGIN:VEVENT",
    `UID:ticket-${event.ticketId}@ticketrush`,
    `DTSTAMP:${utcStamp(event.now)}`,
    `DTSTART:${utcStamp(event.startsAt)}`,
    `DTEND:${utcStamp(event.endsAt)}`,
    `SUMMARY:${escapeIcsText(event.title)}`,
    `LOCATION:${escapeIcsText(event.location)}`,
    `DESCRIPTION:${escapeIcsText(event.description)}`,
    `URL:${event.url}`,
    "END:VEVENT",
    "END:VCALENDAR",
  ];
  return lines.map(foldIcsLine).join(CRLF) + CRLF;
}

/** `dem-nhac-mua-thu.ics` */
export function icsFileName(slug: string): string {
  return `${slug || "ticketrush"}.ics`;
}
