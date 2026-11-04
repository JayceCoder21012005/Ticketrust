import type { QueueStatus } from "./types";

/** Messages the page sends to the polling worker. */
export type WorkerIn =
  | { type: "start"; eventId: string; token: string }
  | { type: "token"; token: string }
  | { type: "pollNow" }
  | { type: "stop" };

/** Messages the polling worker sends back. */
export type WorkerOut =
  | { type: "status"; status: QueueStatus }
  | { type: "unauthorized" }
  | { type: "notJoined" }
  | { type: "error"; failures: number };
