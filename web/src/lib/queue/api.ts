import { api } from "@/lib/api/client";
import { ApiError, type ProblemDetails } from "@/lib/errors";
import { parseStatus } from "./fetch-status";
import type { QueueStatus } from "./types";

/**
 * `POST /events/{id}/queue/join`: idempotent, so reloading the page or opening a second tab is safe. Entering the line takes
 * a Turnstile token (spec 12); a member coming back needs none, but the page cannot tell, so it always sends one.
 */
export async function joinQueue(eventId: string, accessCode?: string, captchaToken?: string | null): Promise<QueueStatus> {
  const result = await api.POST("/api/v1/events/{eventId}/queue/join", {
    params: { path: { eventId } },
    body: { accessCode: accessCode?.trim() || null, captchaToken: captchaToken ?? null },
  });

  if (result.data === undefined) {
    throw new ApiError(result.response.status, (result.error ?? {}) as ProblemDetails);
  }

  return parseStatus(result.data);
}

/** `POST /events/{id}/queue/leave`: frees the buyer's place or shopping slot immediately. */
export async function leaveQueue(eventId: string): Promise<void> {
  const result = await api.POST("/api/v1/events/{eventId}/queue/leave", { params: { path: { eventId } } });
  if (!result.response.ok) {
    throw new ApiError(result.response.status, (result.error ?? {}) as ProblemDetails);
  }
}
