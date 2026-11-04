import type { SeatMap } from "./types";

export { fetchAvailability, type TimedAvailability } from "@/lib/catalog/browser";

/** The static layout (ETag-cached by the browser). Coordinates arrive as JSON decimals; keep them numbers. */
export async function fetchSeatMap(eventId: string, signal?: AbortSignal): Promise<SeatMap> {
  const response = await fetch(`/api/v1/events/${eventId}/seat-map`, { signal, headers: { accept: "application/json" } });
  if (!response.ok) {
    throw new Error(`GET seat-map failed with ${response.status}`);
  }

  const map = (await response.json()) as SeatMap;
  return { ...map, seats: map.seats.map((s) => ({ ...s, x: Number(s.x), y: Number(s.y) })) };
}
