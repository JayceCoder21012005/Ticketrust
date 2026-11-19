import { api } from "@/lib/api/client";
import type { EventSummary } from "@/lib/catalog/types";
import { ApiError, type ProblemDetails } from "@/lib/errors";

/** `GET /me/follows` (UI refresh): followed events that are still on, soonest first. */
export async function getMyFollows(signal?: AbortSignal): Promise<EventSummary[]> {
  const result = await api.GET("/api/v1/me/follows", { signal, cache: "no-store" });
  if (result.data === undefined) {
    throw new ApiError(result.response.status, (result.error ?? {}) as ProblemDetails);
  }

  return result.data as EventSummary[];
}
