import { api } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { ApiError, NetworkError, type ProblemDetails } from "@/lib/errors";

export type ScanResponse = components["schemas"]["ScanResponse"];
export type ScanResult = components["schemas"]["ScanResult"];
export type GateEvent = components["schemas"]["GateEventItem"];
export type GateStats = { checkedIn: number; total: number; lastMinute: number };

const isAbort = (error: unknown) => error instanceof DOMException && error.name === "AbortError";

/** A 429 always carries `retryAfterSeconds`: from the body, else from `Retry-After`. */
function fail(response: Response, error: unknown): ApiError {
  const problem = { ...((error ?? {}) as ProblemDetails) };
  const header = Number(response.headers.get("retry-after"));
  if (response.status === 429 && typeof problem.retryAfterSeconds !== "number" && Number.isFinite(header) && header > 0) {
    problem.retryAfterSeconds = header;
  }

  return new ApiError(response.status, problem);
}

/** A fetch that rejects (no HTTP answer) becomes `NetworkError`; an abort stays an abort. */
async function send<T>(request: () => Promise<T>): Promise<T> {
  try {
    return await request();
  } catch (error) {
    throw isAbort(error) ? error : new NetworkError({ cause: error });
  }
}

/** `GET /gate/events`: the events assigned to the signed-in staff member, tonight's first. */
export async function listGateEvents(signal?: AbortSignal): Promise<GateEvent[]> {
  const result = await send(() => api.GET("/api/v1/gate/events", { signal, cache: "no-store" }));
  if (result.data === undefined) throw fail(result.response, result.error);
  return result.data;
}

/** `POST /gate/events/{eventId}/scan`. 404 `gate.not_found` (not assigned), 429 (too fast), 503 (system busy) are `ApiError`s. */
export async function scanTicket(eventId: string, payload: string, signal?: AbortSignal): Promise<ScanResponse> {
  const result = await send(() => api.POST("/api/v1/gate/events/{eventId}/scan", { params: { path: { eventId } }, body: { payload }, signal }));
  if (result.data === undefined) throw fail(result.response, result.error);
  return result.data;
}

/** `GET /gate/events/{eventId}/stats`: the live counter. The api sends numbers; the generated type allows strings too. */
export async function getGateStats(eventId: string, signal?: AbortSignal): Promise<GateStats> {
  const result = await send(() => api.GET("/api/v1/gate/events/{eventId}/stats", { params: { path: { eventId } }, signal, cache: "no-store" }));
  if (result.data === undefined) throw fail(result.response, result.error);
  const { checkedIn, total, lastMinute } = result.data;
  return { checkedIn: Number(checkedIn), total: Number(total), lastMinute: Number(lastMinute) };
}
