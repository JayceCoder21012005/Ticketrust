import { api } from "@/lib/api/client";
import { ApiError, type ProblemDetails } from "@/lib/errors";
import type { CreateHoldBody, Hold } from "./types";

type RawHold = Omit<Hold, "total" | "seats" | "ga"> & {
  total: number | string;
  seats: (Omit<Hold["seats"][number], "price"> & { price: number | string })[];
  ga: (Omit<Hold["ga"][number], "price"> & { price: number | string })[];
};

/** The generated client types int64 as `number | string`; the page works with numbers. */
function normaliseHold(raw: RawHold): Hold {
  return {
    ...raw,
    total: Number(raw.total),
    seats: raw.seats.map((s) => ({ ...s, price: Number(s.price) })),
    ga: raw.ga.map((g) => ({ ...g, price: Number(g.price) })),
  };
}

const fail = (status: number, error: unknown) => new ApiError(status, (error ?? {}) as ProblemDetails);

/** `POST /events/{id}/holds`. Reuse the same `idempotencyKey` when retrying the same selection (C2). */
export async function createHold(eventId: string, body: CreateHoldBody, admissionToken: string, idempotencyKey: string): Promise<Hold> {
  const result = await api.POST("/api/v1/events/{eventId}/holds", {
    params: { path: { eventId } },
    // Checked by endpoint filters, so they are not in the OpenAPI parameters.
    headers: { "X-Admission-Token": admissionToken, "Idempotency-Key": idempotencyKey },
    body: {
      seatIndexes: body.seatIndexes ?? null,
      ga: body.ga ?? null,
      bestAvailable: body.bestAvailable ?? null,
      replaceExisting: body.replaceExisting,
    },
  });

  if (result.data === undefined) {
    throw fail(result.response.status, result.error);
  }

  return normaliseHold(result.data as RawHold);
}

/** The buyer's live hold, or null. */
export async function getMyHold(eventId: string): Promise<Hold | null> {
  const result = await api.GET("/api/v1/events/{eventId}/holds/me", { params: { path: { eventId } } });
  if (result.response.status === 404) {
    return null;
  }

  if (result.data === undefined) {
    throw fail(result.response.status, result.error);
  }

  return normaliseHold(result.data as RawHold);
}

/** `DELETE /events/{id}/holds/me`: idempotent. */
export async function releaseMyHold(eventId: string): Promise<void> {
  const result = await api.DELETE("/api/v1/events/{eventId}/holds/me", { params: { path: { eventId } } });
  if (!result.response.ok) {
    throw fail(result.response.status, result.error);
  }
}

/** Seat indexes a `hold.seats_unavailable` problem lists; empty for any other error. */
export function unavailableOf(error: unknown): number[] {
  if (!(error instanceof ApiError) || !Array.isArray(error.problem.unavailable)) {
    return [];
  }

  return (error.problem.unavailable as unknown[]).map(Number).filter(Number.isInteger);
}
