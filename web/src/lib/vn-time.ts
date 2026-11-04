/**
 * Organizers enter and read times in Vietnam time (spec 05), never the device's zone: an organizer travelling abroad
 * must still open sales at 10:00 in Hà Nội. Vietnam has no daylight saving, so the offset is a constant +07:00.
 */
const OFFSET_MS = 7 * 60 * 60 * 1000;
const WEEKDAYS = ["CN", "T2", "T3", "T4", "T5", "T6", "T7"];

const pad = (n: number) => String(n).padStart(2, "0");

/** `("2026-12-01", "00:30")` → `"2026-12-01T00:30:00+07:00"`. Throws on a date or time that doesn't exist. */
export function toVietnamIso(date: string, time: string): string {
  const d = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date);
  const t = /^(\d{2}):(\d{2})$/.exec(time);
  if (!d || !t) {
    throw new RangeError(`Invalid Vietnam date/time: ${date} ${time}`);
  }

  const [year, month, day, hour, minute] = [d[1], d[2], d[3], t[1], t[2]].map(Number);
  const probe = new Date(Date.UTC(year, month - 1, day));
  if (probe.getUTCMonth() !== month - 1 || probe.getUTCDate() !== day || hour > 23 || minute > 59) {
    throw new RangeError(`Invalid Vietnam date/time: ${date} ${time}`);
  }

  return `${date}T${time}:00+07:00`;
}

/** The Vietnam wall clock of an instant, as values for `<input type="date">` and `<input type="time">`. */
export function fromIsoToVietnam(iso: string): { date: string; time: string } {
  const vn = new Date(Date.parse(iso) + OFFSET_MS);
  return {
    date: `${vn.getUTCFullYear()}-${pad(vn.getUTCMonth() + 1)}-${pad(vn.getUTCDate())}`,
    time: `${pad(vn.getUTCHours())}:${pad(vn.getUTCMinutes())}`,
  };
}

/** `"19:30, T7 06/12/2026 (GMT+7)"`: the zone is spelled out so nobody reads it as their own. */
export function formatVietnam(iso: string): string {
  const vn = new Date(Date.parse(iso) + OFFSET_MS);
  const time = `${pad(vn.getUTCHours())}:${pad(vn.getUTCMinutes())}`;
  const date = `${pad(vn.getUTCDate())}/${pad(vn.getUTCMonth() + 1)}/${vn.getUTCFullYear()}`;
  return `${time}, ${WEEKDAYS[vn.getUTCDay()]} ${date} (GMT+7)`;
}
