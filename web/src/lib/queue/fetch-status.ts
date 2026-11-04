import type { PollOutcome } from "./poller";
import type { QueueState, QueueStatus } from "./types";

type RawStatus = Record<string, unknown>;

const toNumber = (value: unknown): number | null => (value === null || value === undefined ? null : Number(value));

/** The api sends int64 as strings in some cases; normalise once at the edge. */
export function parseStatus(raw: unknown): QueueStatus {
  const body = raw as RawStatus;
  return {
    status: body.status as QueueState,
    position: toNumber(body.position),
    aheadOfYou: toNumber(body.aheadOfYou),
    estimatedWaitSeconds: toNumber(body.estimatedWaitSeconds),
    opensAt: body.opensAt as string,
    serverNow: body.serverNow as string,
    pollAfterMs: Number(body.pollAfterMs),
    admissionToken: (body.admissionToken as string | null) ?? null,
    tokenExpiresAt: (body.tokenExpiresAt as string | null) ?? null,
  };
}

/**
 * One poll of `/queue/me`, as plain `fetch` so it runs in a Web Worker (no React, no session module). The caller
 * supplies the access token because a worker cannot read the page's in-memory session.
 */
export async function fetchQueueStatus(eventId: string, accessToken: string, fetchImpl: typeof fetch = fetch): Promise<PollOutcome> {
  try {
    const response = await fetchImpl(`/api/v1/events/${eventId}/queue/me`, {
      cache: "no-store",
      headers: { accept: "application/json", authorization: `Bearer ${accessToken}` },
    });

    if (response.status === 401) {
      return { kind: "unauthorized" };
    }

    if (response.status === 404) {
      return (await codeOf(response)) === "queue.not_joined" ? { kind: "notJoined" } : { kind: "gone" };
    }

    if (!response.ok) {
      return { kind: "error", httpStatus: response.status };
    }

    return { kind: "ok", status: parseStatus(await response.json()) };
  } catch {
    return { kind: "error", httpStatus: 0 };
  }
}

async function codeOf(response: Response): Promise<string | undefined> {
  try {
    return ((await response.json()) as { code?: string }).code;
  } catch {
    return undefined;
  }
}
