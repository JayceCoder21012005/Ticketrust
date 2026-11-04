import { LOCALE } from "@/lib/format";
import { t } from "@/lib/i18n";

/** "#1.234": the place in line, with Vietnamese thousands separators. */
export function formatPosition(position: number): string {
  return `#${position.toLocaleString(LOCALE)}`;
}

/** Waits are estimates, so they are rounded and prefixed with "khoảng" instead of pretending to be exact. */
export function etaLabel(seconds: number): string {
  if (seconds < 60) {
    return t("queue.waiting.etaUnderMinute");
  }

  const minutes = Math.round(seconds / 60);
  if (minutes < 90) {
    return t("queue.waiting.etaMinutes", "vi", { minutes });
  }

  return t("queue.waiting.etaHours", "vi", { hours: Math.round(seconds / 3600) });
}

/** 0 when you joined, 100 at the front of the line. */
export function progressPercent(startPosition: number | null, position: number): number {
  if (!startPosition || startPosition <= 1) {
    return position <= 1 && startPosition ? 100 : 0;
  }

  return Math.round(Math.min(100, Math.max(0, ((startPosition - position) / (startPosition - 1)) * 100)));
}
