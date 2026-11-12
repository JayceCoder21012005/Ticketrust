import { api } from "@/lib/api/client";
import { ApiError, NetworkError, type ProblemDetails } from "@/lib/errors";
import type { QrKey, TicketDetail, TicketEventGroup, TicketFailure, TicketScope } from "./types";

/**
 * An api refusal as `ApiError`. A 429 always carries `retryAfterSeconds`: from the body, else from `Retry-After`.
 */
function fail(response: Response, error: unknown): ApiError {
  const problem = { ...((error ?? {}) as ProblemDetails) };
  const header = Number(response.headers.get("retry-after"));
  if (response.status === 429 && typeof problem.retryAfterSeconds !== "number" && Number.isFinite(header) && header > 0) {
    problem.retryAfterSeconds = header;
  }

  return new ApiError(response.status, problem);
}

const isAbort = (error: unknown) => error instanceof DOMException && error.name === "AbortError";

/** Runs a request; a fetch that rejects (no HTTP answer) becomes `NetworkError`, an abort stays an abort. */
async function send<T>(request: () => Promise<T>): Promise<T> {
  try {
    return await request();
  } catch (error) {
    throw isAbort(error) ? error : new NetworkError({ cause: error });
  }
}

/** `GET /me/tickets?scope=…`: the user's tickets grouped by event (upcoming by start ascending, past descending). */
export async function getMyTickets(scope: TicketScope, signal?: AbortSignal): Promise<TicketEventGroup[]> {
  const result = await send(() => api.GET("/api/v1/me/tickets", { params: { query: { scope } }, signal, cache: "no-store" }));
  if (result.data === undefined) {
    throw fail(result.response, result.error);
  }

  return result.data.events;
}

/** `GET /tickets/{id}`: 404 `ticket.not_found` for an unknown id and for someone else's ticket alike. */
export async function getTicket(id: string, signal?: AbortSignal): Promise<TicketDetail> {
  const result = await send(() => api.GET("/api/v1/tickets/{id}", { params: { path: { id } }, signal, cache: "no-store" }));
  if (result.data === undefined) {
    throw fail(result.response, result.error);
  }

  return result.data;
}

/**
 * `GET /tickets/{id}/qr-key`. Called imperatively by the wallet (never a cached query: it is a credential). 409
 * `ticket.not_displayable` when the ticket cannot show a QR, 429 with `retryAfterSeconds`, `NetworkError` when offline.
 */
export async function getQrKey(id: string, signal?: AbortSignal): Promise<QrKey> {
  const result = await send(() => api.GET("/api/v1/tickets/{id}/qr-key", { params: { path: { id } }, signal, cache: "no-store" }));
  if (result.data === undefined) {
    throw fail(result.response, result.error);
  }

  // The generated client types int32 as `number | string`.
  return { ...result.data, stepSeconds: Number(result.data.stepSeconds) };
}

const byCode: Record<string, TicketFailure> = {
  "ticket.not_found": "not_found",
  "ticket.not_displayable": "not_displayable",
  "ticket.invalid_scope": "invalid_scope",
  "ticket.qr_key_rate_limited": "rate_limited",
  "system.busy": "busy",
};

const byStatus: Record<number, TicketFailure> = { 404: "not_found", 409: "not_displayable", 429: "rate_limited", 503: "busy" };

/** Classifies an error thrown by this module. */
export function ticketFailure(error: unknown): TicketFailure {
  if (error instanceof NetworkError) {
    return "network";
  }
  if (!(error instanceof ApiError)) {
    return "other";
  }

  return (error.code && byCode[error.code]) || byStatus[error.status] || "other";
}

/** Seconds to wait after a 429, or null when the error is not a rate limit. */
export function retryAfterSeconds(error: unknown): number | null {
  if (!(error instanceof ApiError) || error.status !== 429) {
    return null;
  }

  const seconds = error.problem.retryAfterSeconds;
  return typeof seconds === "number" ? seconds : null;
}
