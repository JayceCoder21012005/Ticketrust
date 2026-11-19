import { api } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { ApiError, type ProblemDetails } from "@/lib/errors";
import type { Hold } from "@/lib/seats/types";

type Schemas = components["schemas"];

/** Numbers come back as numbers (the schema also allows numeric strings for int32). */
export type WaitlistEntryStatus = Omit<Schemas["WaitlistStatusResponse"], "position" | "quantity"> & { position: number | null; quantity: number };
export type MyWaitlistItem = Omit<Schemas["MyWaitlistItem"], "entry"> & { entry: WaitlistEntryStatus };
export type WaitlistOffer = Omit<Schemas["WaitlistOfferResponse"], "hold"> & { hold: Hold };
export type AcceptedOffer = Schemas["AcceptedWaitlistOffer"];

const fail = (status: number, error: unknown) => new ApiError(status, (error ?? {}) as ProblemDetails);

/** `GET /events/{id}/waitlist/me`: the caller's live entry, or null when they are not on the list. */
export async function getMyWaitlistEntry(eventId: string, signal?: AbortSignal): Promise<WaitlistEntryStatus | null> {
  const result = await api.GET("/api/v1/events/{id}/waitlist/me", { params: { path: { id: eventId } }, signal, cache: "no-store" });
  if (result.response.status === 404) return null;
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data as WaitlistEntryStatus;
}

/** `POST /events/{id}/waitlist`. 409 `waitlist.not_available` while tickets remain; 409 `waitlist.already_joined`. */
export async function joinWaitlist(eventId: string, tierId: string | null, quantity: number): Promise<WaitlistEntryStatus> {
  const result = await api.POST("/api/v1/events/{id}/waitlist", { params: { path: { id: eventId } }, body: { tierId, quantity } });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data as WaitlistEntryStatus;
}

/** `DELETE /events/{id}/waitlist/me` (204, idempotent). A live offer goes to the next in line. */
export async function leaveWaitlist(eventId: string): Promise<void> {
  const result = await api.DELETE("/api/v1/events/{id}/waitlist/me", { params: { path: { id: eventId } } });
  if (!result.response.ok) throw fail(result.response.status, "error" in result ? result.error : undefined);
}

/** `GET /me/waitlist`: the caller's live entries, soonest event first. */
export async function listMyWaitlist(signal?: AbortSignal): Promise<MyWaitlistItem[]> {
  const result = await api.GET("/api/v1/me/waitlist", { signal, cache: "no-store" });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data as MyWaitlistItem[];
}

/** `GET /waitlist/offers/{offerId}`. 404 `waitlist.offer_not_found`; 409 `waitlist.offer_expired`. */
export async function getWaitlistOffer(offerId: string, signal?: AbortSignal): Promise<WaitlistOffer> {
  const result = await api.GET("/api/v1/waitlist/offers/{offerId}", { params: { path: { offerId } }, signal, cache: "no-store" });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data as WaitlistOffer;
}

/** `POST /waitlist/offers/{offerId}/accept`: what checkout needs to continue from the held tickets. */
export async function acceptWaitlistOffer(offerId: string): Promise<AcceptedOffer> {
  const result = await api.POST("/api/v1/waitlist/offers/{offerId}/accept", { params: { path: { offerId } } });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data;
}
