import { fetchQueueStatus } from "./fetch-status";
import { createPoller, type PollerEvents } from "./poller";
import type { WorkerIn, WorkerOut } from "./worker-protocol";

export type TransportHandlers = PollerEvents;

/** How the page talks to whatever is polling for it: a Web Worker, or the main thread when none exists. */
export type QueueTransport = {
  start: (eventId: string, token: string) => void;
  setToken: (token: string) => void;
  pollNow: () => void;
  stop: () => void;
};

export type WorkerLike = {
  postMessage: (message: WorkerIn) => void;
  terminate: () => void;
  onmessage: ((event: MessageEvent<WorkerOut>) => void) | null;
};

function defaultWorkerFactory(): WorkerLike | null {
  if (typeof Worker === "undefined") {
    return null;
  }

  try {
    return new Worker(new URL("./queue-worker.ts", import.meta.url), { type: "module" }) as unknown as WorkerLike;
  } catch {
    return null;
  }
}

export function createQueueTransport(handlers: TransportHandlers, createWorker: () => WorkerLike | null = defaultWorkerFactory): QueueTransport {
  let worker: WorkerLike | null = null;
  let inline: { eventId: string; poller: ReturnType<typeof createPoller> } | null = null;

  const route = (message: WorkerOut) => {
    switch (message.type) {
      case "status":
        return handlers.onStatus(message.status);
      case "unauthorized":
        return handlers.onUnauthorized();
      case "notJoined":
        return handlers.onNotJoined();
      case "error":
        return handlers.onError(message.failures);
    }
  };

  const post = (message: WorkerIn) => worker?.postMessage(message);

  return {
    start(eventId, token) {
      if (!worker && !inline) {
        worker = createWorker();
        if (worker) {
          worker.onmessage = (event) => route(event.data);
        }
      }

      if (worker) {
        post({ type: "start", eventId, token });
        return;
      }

      inline?.poller.stop();
      inline = {
        eventId,
        poller: createPoller({ poll: (accessToken) => fetchQueueStatus(eventId, accessToken), events: handlers }),
      };
      inline.poller.start(token);
    },
    setToken(token) {
      post({ type: "token", token });
      inline?.poller.setToken(token);
    },
    pollNow() {
      post({ type: "pollNow" });
      inline?.poller.pollNow();
    },
    stop() {
      post({ type: "stop" });
      worker?.terminate();
      worker = null;
      inline?.poller.stop();
      inline = null;
    },
  };
}
