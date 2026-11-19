# Spec 13 — Sold-out waitlist: design

Date: 2026-11-19 · Status: owner asked to build it directly ("làm spec 13"); the calls below are mine, listed for review.
Source: `specs/13-sold-out-waitlist.md`.

## What exists and what does not

- Nothing voids a paid ticket today (`Ticket.Void()` has no caller; admin refunds are spec 16) and resale (spec 14) is not built,
  so neither `InventoryReturned` nor `ListingCreated` exists.
- Inventory lives in Redis (spec 07): `sold` SET of seat indexes, `held` HASH seat → holdId, hold HASHes with an `expiry` ZSET
  swept by `HoldExpiryService`, GA `remaining` per tier and `gaheld` per tier, `bought:<user>` per user. A periodic reconcile
  rebuilds `sold`/`bought` from the database.

## Calls

1. **Returned source.** `Ticket.Void()` raises `TicketReturned(eventId, ticketId, tierId, seatIndex?, ownerUserId)`; the outbox
   turns it into the `InventoryReturned` integration event that spec 13 consumes. Spec 16 (admin refund) and 14 (resale) only
   have to call `Void()` / publish `ListingCreated` later. No endpoint voids a ticket in this spec. Resale offers are out of
   scope until 14 exists (the domain keeps `Source` and `ListingId` for it).
2. **Atomic hand-over in Redis.** A returned seat never passes through "free": `waitlist_offer.lua` moves it from `sold` (or from
   free, if a reconcile already freed it) straight into a new hold of the waitlist user, and decrements the previous owner's
   `bought`. GA: `gaheld += qty` (remaining unchanged: sold → held). A seat held by someone else is refused (`TAKEN`).
3. **Offer holds are owned by the waitlist, not the sweeper.** The hold HASH carries `waitlist=1` and is **not** added to the
   `expiry` ZSET, so `HoldExpiryService` never frees it. `WaitlistOfferService` expires offers from the database
   (`expires_at`) and, in `waitlist_pass.lua`, moves the hold's seats/GA atomically to the next entry's new hold, or releases
   them to the public when nobody matches.
4. **Offer TTL 15 min**, admission token 15 min (`IAdmissionTokenFactory`), both from `WaitlistOptions`.
5. **Matching:** oldest `Waiting` entry (FIFO by `created_at`, v7 id) whose `tierId` is null or equal and whose `quantity` is
   ≤ the returned units of that tier; a returned *seat* is one unit. Entries for more units than are returned wait.
6. **Second chance:** an unused offer sends the entry back to `Waiting` once (new `created_at` = now, so it goes to the end); a
   second miss → `Expired`.
7. **Join rules:** confirmed email; event published and (`is_sold_out` or every phase closed) and not started; one live entry
   per (event, user) (unique partial index); quantity 1–4 and ≤ the phase cap minus tickets already bought.
8. **Accept:** `POST /waitlist/offers/{id}/accept` → `{admissionToken, holdId, eventSlug, expiresAt}`; the web stores the
   admission like the queue does and opens `/events/{slug}/checkout`, which already works from the user's hold (spec 08).
   Converting happens when the order is paid (`OrderPaid` for that hold → entry `Converted`).
9. **Notifications (09):** `WaitlistOfferIssued` → in-app + email "Có vé cho bạn!" with the link `/waitlist/offers/{id}`.

## API

| Method | Route | Answer |
|---|---|---|
| POST | `/events/{id}/waitlist` | `{tierId?, quantity}` → `WaitlistStatus {position, status, entryId, offer?}`; 409 `waitlist.not_available` / `waitlist.already_joined`; 403 email |
| GET | `/events/{id}/waitlist/me` | `WaitlistStatus` or 404 |
| DELETE | `/events/{id}/waitlist/me` | 204 (an active offer is released/passed on) |
| GET | `/me/waitlist` | the caller's live entries with event info (for `/account/waitlist`) |
| GET | `/waitlist/offers/{offerId}` | offer details for the offer page |
| POST | `/waitlist/offers/{offerId}/accept` | `{admissionToken, holdId, eventSlug, expiresAt}`; 409 `waitlist.offer_expired` |

`position` = number of older `Waiting` entries for the event + 1.

## Web

- Event page CTA when sold out (or every phase closed): "Vào danh sách chờ" → `Dialog` (tier select incl. "Hạng bất kỳ",
  quantity stepper 1–4, how offers work). Joined: position and "Rời danh sách chờ".
- `/account/waitlist` in the account hub nav: entries with position/status, active offer link.
- `/waitlist/offers/[id]`: "Có vé cho bạn!", 15:00 countdown, tier/seat and price, "Mua ngay" → checkout.

## Tests (acceptance)

- 3 waiting users, 1 returned ticket → only the oldest matching user gets an offer (integration, real Redis + Postgres).
- Expired offer passes to the next user within 5 s and the seat is never held for two users at once (Lua + service test).
- Joining while tickets remain → `waitlist.not_available`.
- Resale-window criterion: deferred to spec 14 (no listings exist).
