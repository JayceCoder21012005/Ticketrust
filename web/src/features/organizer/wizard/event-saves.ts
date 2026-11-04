import type { QueryClient } from "@tanstack/react-query";
import type { OrganizerEvent } from "../types";
import type { AutosaveStatus } from "./use-autosave";

/**
 * The one save queue of an event in this tab (spec 05: never two saves at once). It lives beside the query client
 * rather than in a component, because the app router remounts the wizard page on every step change while the step
 * just left may still be saving. Every save runs after the previous one and reads the newest version at that moment:
 * the newer of this tab's last save response and the event the step loaded (another tab may have saved since, and the
 * step shows that data), so queued saves and saves across steps never send a stale version.
 */
export class EventSaves {
  private latest: OrganizerEvent | null = null;
  private tail: Promise<unknown> = Promise.resolve();
  private generation = 0;
  private taskGeneration = 0;
  private failure: AutosaveStatus | null = null;
  private readonly listeners = new Set<() => void>();

  /** Queues `task`; it gets the newer of this tab's last saved event and `loaded()` (the event the step shows). */
  run(task: (current: OrganizerEvent) => Promise<void>, loaded: () => OrganizerEvent): Promise<void> {
    const generation = this.generation;
    const next = this.tail.then(() => {
      if (generation !== this.generation) return undefined;
      this.taskGeneration = generation;
      return task(newer(this.latest, loaded()));
    });
    this.tail = next.catch(() => undefined);
    return next;
  }

  /** A save's response: the next save sends its version. Call it before anything awaits. */
  saved(event: OrganizerEvent) {
    // A save that was already in flight when the organizer reloaded must not override the reloaded event.
    if (this.taskGeneration === this.generation) {
      this.latest = event;
    }
  }

  /** Reload: saves still queued are dropped and the next one starts from the reloaded event. */
  discard() {
    this.generation++;
    this.latest = null;
    this.reportLeft(null);
  }

  /** The status of a step's save that finished after the step was left: the header shows a failure until it's fixed. */
  reportLeft = (status: AutosaveStatus | null) => {
    const failure = status?.state === "error" ? status : null;
    if (failure === this.failure) return;
    this.failure = failure;
    this.listeners.forEach((listener) => listener());
  };

  leftFailure = () => this.failure;

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => void this.listeners.delete(listener);
  };
}

/** Versions are Postgres xmin values: they only grow (wraparound after ~4 billion writes is ignored). */
function newer(saved: OrganizerEvent | null, loaded: OrganizerEvent): OrganizerEvent {
  return saved !== null && Number(saved.version) > Number(loaded.version) ? saved : loaded;
}

const registry = new WeakMap<QueryClient, Map<string, EventSaves>>();

/** The save queue of `eventId`, shared by every step mounted with this query client. */
export function eventSaves(queryClient: QueryClient, eventId: string): EventSaves {
  let byEvent = registry.get(queryClient);
  if (!byEvent) {
    byEvent = new Map();
    registry.set(queryClient, byEvent);
  }

  let saves = byEvent.get(eventId);
  if (!saves) {
    saves = new EventSaves();
    byEvent.set(eventId, saves);
  }

  return saves;
}
