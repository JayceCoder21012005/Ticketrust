const KEY = "tr.checkout-events";
const MAX_REMEMBERED = 5;

export type RememberedEvent = { id: string; slug: string; title: string };

function read(): RememberedEvent[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(KEY) ?? "[]");
    return Array.isArray(parsed) ? (parsed as RememberedEvent[]).filter((e) => typeof e?.id === "string" && typeof e.slug === "string") : [];
  } catch {
    return [];
  }
}

/**
 * The order api names the event by id only, and an event is read by slug. The seat flow knows both, so it leaves them
 * here for the checkout pages ("back to the seats", the event title). Missing entries (another browser, cleared
 * storage) just fall back to the event list; nothing depends on them.
 */
export function rememberEvent(event: RememberedEvent): void {
  try {
    const rest = read().filter((e) => e.id !== event.id);
    localStorage.setItem(KEY, JSON.stringify([event, ...rest].slice(0, MAX_REMEMBERED)));
  } catch {
    // Storage is a convenience only (private window, blocked site data).
  }
}

export function recallEvent(eventId: string): RememberedEvent | null {
  return read().find((e) => e.id === eventId) ?? null;
}
