/** What the waiting room tells a buyer's client (`GET /events/{id}/queue/me`), with int64 fields as numbers. */
export type QueueState = "Pre" | "Waiting" | "Admitted" | "AllHeld" | "SoldOut" | "Closed" | "Expired";

export type QueueStatus = {
  status: QueueState;
  /** 1 = next in line; null outside `Waiting`. */
  position: number | null;
  aheadOfYou: number | null;
  estimatedWaitSeconds: number | null;
  opensAt: string;
  /** Server clock at the time of the response, for countdowns that ignore the device clock (C10). */
  serverNow: string;
  /** When to ask again, chosen by the server so it can shed load. */
  pollAfterMs: number;
  admissionToken: string | null;
  tokenExpiresAt: string | null;
};

/** Statuses after which polling is pointless: the buyer has to join again (or the sale is over). */
export const TERMINAL_STATES: readonly QueueState[] = ["Closed", "Expired"];
