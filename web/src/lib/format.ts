/** All dates are displayed in Vietnam time, whatever the device's time zone (spec 00). */
export const TIME_ZONE = "Asia/Ho_Chi_Minh";
export const LOCALE = "vi-VN";

type DateInput = Date | string | number;

const vnd = new Intl.NumberFormat(LOCALE, { style: "currency", currency: "VND" });
const time = new Intl.DateTimeFormat(LOCALE, { timeZone: TIME_ZONE, hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
const date = new Intl.DateTimeFormat(LOCALE, { timeZone: TIME_ZONE, day: "2-digit", month: "2-digit", year: "numeric" });
const eventDate = new Intl.DateTimeFormat(LOCALE, {
  timeZone: TIME_ZONE,
  weekday: "long",
  day: "numeric",
  month: "long",
  year: "numeric",
});

/** VND has no minor unit: the amount is whole dong (backend `Money.Amount`). */
export function formatVnd(amount: number): string {
  return vnd.format(amount);
}

export function formatTime(value: DateInput): string {
  return time.format(new Date(value));
}

export function formatDateTime(value: DateInput): string {
  return `${formatTime(value)} ${date.format(new Date(value))}`;
}

export function formatEventDate(value: DateInput): string {
  return eventDate.format(new Date(value));
}

const tileParts = new Intl.DateTimeFormat("en-US", { timeZone: TIME_ZONE, day: "numeric", month: "numeric" });

/** `20:00 15/11`: sale-opening badges, where the year is obvious. */
export function formatDayMonth(value: DateInput): string {
  // vi-VN renders day + month alone as "15-11"; the slash form matches formatDateTime.
  return formatDateTime(value).replace(/\/\d{4}$/, "");
}

export function formatTimeRange(start: DateInput, end: DateInput): string {
  return `${formatTime(start)} – ${formatTime(end)}`;
}

/** Month and day for the Luma-style calendar tile on the event page ("Th12" / "15"). */
export function calendarTile(value: DateInput): { month: string; day: string } {
  const parts = tileParts.formatToParts(new Date(value));
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? "";
  return { month: `Th${part("month")}`, day: part("day") };
}
