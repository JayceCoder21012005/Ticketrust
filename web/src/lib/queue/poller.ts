import { nextDelayMs } from "./poll-schedule";
import { TERMINAL_STATES, type QueueStatus } from "./types";

export type PollOutcome =
  | { kind: "ok"; status: QueueStatus }
  | { kind: "unauthorized" }
  | { kind: "notJoined" }
  /** The event no longer exists for buyers (cancelled or unpublished): retrying will never help. */
  | { kind: "gone" }
  | { kind: "error"; httpStatus: number };

export type PollerEvents = {
  onStatus: (status: QueueStatus) => void;
  /** The access token expired: the owner must refresh it and call `setToken`. */
  onUnauthorized: () => void;
  onNotJoined: () => void;
  /** Consecutive failures so far. */
  onError: (failures: number) => void;
};

type PollerDeps = {
  poll: (accessToken: string) => Promise<PollOutcome>;
  events: PollerEvents;
  random?: () => number;
};

export type Poller = {
  start: (accessToken: string) => void;
  /** Resumes a poller that paused on 401. */
  setToken: (accessToken: string) => void;
  stop: () => void;
  pollNow: () => void;
};

/**
 * Polling loop of the waiting room. It follows the server's `pollAfterMs`, backs off on errors, pauses on 401 until a
 * fresh token arrives and stops on terminal states. Timer-based and free of DOM/React, so the same loop runs inside
 * a Web Worker (background tabs throttle main-thread timers) or on the main thread where workers are unavailable.
 */
export function createPoller({ poll, events, random = Math.random }: PollerDeps): Poller {
  let token = "";
  let generation = 0;
  let failures = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let inFlight = false;
  let active = false;

  const clear = () => {
    clearTimeout(timer);
    timer = undefined;
  };

  const schedule = (delay: number) => {
    clear();
    timer = setTimeout(() => void run(), delay);
  };

  const run = async () => {
    if (!active || inFlight) {
      return;
    }

    clear();
    inFlight = true;
    const mine = generation;
    let outcome: PollOutcome;
    try {
      outcome = await poll(token);
    } catch {
      outcome = { kind: "error", httpStatus: 0 };
    }

    inFlight = false;
    if (!active || mine !== generation) {
      return;
    }

    switch (outcome.kind) {
      case "ok":
        failures = 0;
        events.onStatus(outcome.status);
        if (TERMINAL_STATES.includes(outcome.status.status)) {
          active = false;
          return;
        }

        schedule(nextDelayMs(outcome.status, 0, random));
        return;
      case "unauthorized":
        events.onUnauthorized();
        return;
      case "notJoined":
      case "gone":
        active = false;
        events.onNotJoined();
        return;
      case "error":
        failures += 1;
        events.onError(failures);
        schedule(nextDelayMs(null, failures, random));
        return;
    }
  };

  return {
    start(accessToken) {
      token = accessToken;
      generation += 1;
      failures = 0;
      active = true;
      inFlight = false;
      void run();
    },
    setToken(accessToken) {
      token = accessToken;
      if (active) {
        void run();
      }
    },
    stop() {
      active = false;
      generation += 1;
      clear();
    },
    pollNow() {
      void run();
    },
  };
}
