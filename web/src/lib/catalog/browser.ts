import { clockOffsetMs } from "@/lib/clock";
import type { Availability, EventPage } from "./types";

/** Browser-side catalog calls go through the same-origin `/api` rewrite. */
async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(`/api/v1${path}`, { cache: "no-store", signal, headers: { accept: "application/json" } });
  if (!response.ok) {
    throw new Error(`GET /api/v1${path} failed with ${response.status}`);
  }

  return (await response.json()) as T;
}

export function fetchEventPage(query: Record<string, string>, cursor: string, signal?: AbortSignal): Promise<EventPage> {
  const params = new URLSearchParams({ ...query, cursor });
  return getJson<EventPage>(`/events?${params}`, signal);
}

export type TimedAvailability = Availability & { offsetMs: number };

/**
 * Live availability plus the clock offset measured on the same response (C10). The midpoint of request and
 * response is the best guess of when the server stamped `serverNow`.
 */
export async function fetchAvailability(eventId: string, signal?: AbortSignal): Promise<TimedAvailability> {
  const sentAt = Date.now();
  const availability = await getJson<Availability>(`/events/${eventId}/availability`, signal);
  const receivedAt = Date.now();
  return { ...availability, offsetMs: clockOffsetMs(availability.serverNow, (sentAt + receivedAt) / 2) };
}

let clockOffset: Promise<number> | undefined;

/** One `/time` request per page view, shared by every countdown on the page. Falls back to the device clock. */
export function loadClockOffset(): Promise<number> {
  clockOffset ??= (async () => {
    const sentAt = Date.now();
    const { serverNow } = await getJson<{ serverNow: string }>("/time");
    return clockOffsetMs(serverNow, (sentAt + Date.now()) / 2);
  })().catch(() => 0);
  return clockOffset;
}
