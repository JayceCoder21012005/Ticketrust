import { ApiError } from "@/lib/errors";

/**
 * True when the api has not given a final answer to the request, so a retry is "the same request" (C2): a network
 * failure, a 5xx (the filter never stores those) or `idempotency.in_progress` (the first copy is still running).
 */
export function isUndecided(error: unknown): boolean {
  if (!(error instanceof ApiError)) {
    return true;
  }

  return error.status >= 500 || error.code === "idempotency.in_progress";
}

/**
 * One Idempotency-Key per request the buyer means (spec 08, C2). The same key is handed out again while the answer is
 * undecided (double click, network blip) so the api replays or joins the first request instead of running it twice.
 * Once the api answered, the key is dropped: it has stored that answer and would replay it, and a retry after a refusal
 * or a failed payment must run again, not replay a dead gateway session.
 *
 * `signature` names what is being asked; asking for something else gets a fresh key even before any answer.
 */
export function createKeyKeeper(newKey: () => string = () => crypto.randomUUID()) {
  let current: { signature: string; key: string } | null = null;

  return {
    acquire(signature = ""): string {
      if (current?.signature !== signature) {
        current = { signature, key: newKey() };
      }

      return current.key;
    },
    /** Reports how the request ended: `null` for success. */
    settle(error: unknown | null): void {
      if (error === null || !isUndecided(error)) {
        current = null;
      }
    },
  };
}
