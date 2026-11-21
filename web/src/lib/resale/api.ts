import { api } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { ApiError, type ProblemDetails } from "@/lib/errors";

type Schemas = components["schemas"];

/** int64 money comes back as a number (the schema also allows numeric strings). */
type Numeric<T, K extends keyof T> = Omit<T, K> & { [P in K]: number };

export type Listing = Numeric<Schemas["ListingResponse"], "price">;
export type PublicListing = Numeric<Schemas["PublicListing"], "price">;
export type ListingTierGroup = Omit<Numeric<Schemas["ListingTierGroup"], "faceValue">, "listings"> & { listings: PublicListing[] };
export type Transfer = Schemas["TransferResponse"];
export type TransferInvite = Schemas["TransferInvite"];

const fail = (status: number, error: unknown) => new ApiError(status, (error ?? {}) as ProblemDetails);

/** `POST /tickets/{id}/listings`: 400 `resale.price_above_cap`; 409 `resale.not_allowed`. */
export async function createListing(ticketId: string, price: number): Promise<Listing> {
  const result = await api.POST("/api/v1/tickets/{id}/listings", { params: { path: { id: ticketId } }, body: { price } });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data as Listing;
}

/** `DELETE /listings/{id}` (204, idempotent once cancelled). 409 `resale.listing_unavailable` while someone is paying. */
export async function cancelListing(listingId: string): Promise<void> {
  const result = await api.DELETE("/api/v1/listings/{id}", { params: { path: { id: listingId } } });
  if (!result.response.ok) throw fail(result.response.status, "error" in result ? result.error : undefined);
}

/** `GET /events/{id}/listings` (public): active listings past the waitlist window, by tier, cheapest first. */
export async function listEventListings(eventId: string, signal?: AbortSignal): Promise<ListingTierGroup[]> {
  const result = await api.GET("/api/v1/events/{id}/listings", { params: { path: { id: eventId } }, signal, cache: "no-store" });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data as ListingTierGroup[];
}

/** `POST /listings/{id}/reserve`: a 10-minute reservation and its resale order, paid at `/checkout/{orderId}`. */
export async function reserveListing(listingId: string, idempotencyKey: string): Promise<{ id: string }> {
  const result = await api.POST("/api/v1/listings/{id}/reserve", {
    params: { path: { id: listingId } },
    headers: { "Idempotency-Key": idempotencyKey },
  });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return { id: result.data.id };
}

/** `POST /tickets/{id}/transfers`: 400 `transfer.self`; 409 `transfer.not_allowed`. */
export async function createTransfer(ticketId: string, toEmail: string): Promise<Transfer> {
  const result = await api.POST("/api/v1/tickets/{id}/transfers", { params: { path: { id: ticketId } }, body: { toEmail } });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data;
}

/** `DELETE /transfers/{id}` (204). */
export async function cancelTransfer(transferId: string): Promise<void> {
  const result = await api.DELETE("/api/v1/transfers/{id}", { params: { path: { id: transferId } } });
  if (!result.response.ok) throw fail(result.response.status, "error" in result ? result.error : undefined);
}

/** `GET /transfers/{id}`: the recipient's view. 404 for anyone else (another email, unconfirmed). */
export async function getTransfer(transferId: string, signal?: AbortSignal): Promise<TransferInvite> {
  const result = await api.GET("/api/v1/transfers/{id}", { params: { path: { id: transferId } }, signal, cache: "no-store" });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data;
}

/** `POST /transfers/{id}/accept`: the ticket becomes the caller's (a new QR). Accepting again answers the same. */
export async function acceptTransfer(transferId: string): Promise<TransferInvite> {
  const result = await api.POST("/api/v1/transfers/{id}/accept", { params: { path: { id: transferId } } });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data;
}
