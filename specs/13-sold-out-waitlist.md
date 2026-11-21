# 13 — Sold-out Waitlist

**Goal:** when an event sells out, fans join a waitlist. Any ticket that comes back later goes **first to the waitlist in FIFO order** before anyone else can buy it.

**Depends on:** 08, 09 (14 adds resale offers)

## When tickets come back

Holds that expire during the sale are **not** waitlist inventory. They return to active shoppers in the normal flow (06 `AllHeld` → `Open`). The waitlist gets:
1. Tickets voided after payment (refund approved by admin, 16) → `InventoryReturned`.
2. New resale listings (14) → `ListingCreated` (10-minute exclusive window for the waitlist).

## Domain

- `WaitlistEntry(eventId, tierId?, quantity 1–4, userId, createdAt, status)`. Status: `Waiting → Offered → Converted | Expired → Waiting (back at the end, once) | Left`. Unique `(eventId, userId)` among non-final statuses.
- `WaitlistOffer(entryId, source: Returned|Resale, holdId?, listingId?, expiresAt)`.

## Rules

- Only possible when the event is `SoldOut` (06 state) or the sale phase has closed before the event.
- **WaitlistOfferService** (Worker, leader, consumes `InventoryReturned` / `ListingCreated`):
  1. Find the oldest `Waiting` entry whose tier matches (or any tier) and whose quantity fits.
  2. Returned tickets: create a hold for that user via `hold_create.lua` with TTL 15 min. Resale: reserve the listing for 10 min (14).
  3. Issue an admission token for 15 min (`AdmissionTokenFactory`), mark the entry `Offered`, notify (in-app + email, 09).
  4. Offer expires → release the hold or listing reservation, entry → `Expired`, move on to the next entry.
- An entry that receives an offer and doesn't use it goes back to `Waiting` once (at the end). A second miss → `Expired`.
- A user who already holds the max tickets for the event can't join.

## API (auth + confirmed email)

| Method | Route | Notes |
|---|---|---|
| POST | `/events/{id}/waitlist` | `{tierId?, quantity}` → `{position, status}` |
| GET | `/events/{id}/waitlist/me` | Position (count of older `Waiting` entries), status, active offer |
| DELETE | `/events/{id}/waitlist/me` | Leave |
| POST | `/waitlist/offers/{offerId}/accept` | Returns the admission token + hold → continue in checkout (08) |

Error codes: `waitlist.not_available`, `waitlist.already_joined`, `waitlist.offer_expired`.

## Frontend

- Sold-out event page: "Hết vé" badge, CTA "Vào danh sách chờ" → `Dialog` (tier select, quantity stepper, explanation of how offers work).
- `/account/waitlist`: entries with position and status.
- Offer: notification → `/waitlist/offers/[id]`: "Có vé cho bạn!", countdown 15:00, seat/tier and price, "Mua ngay" → checkout.

## Acceptance criteria

- [x] With 3 waiting users and 1 returned ticket, only the oldest matching user gets an offer.
- [x] An expired offer passes to the next user within 5 s, and the ticket is never offered to two users at once.
- [x] A resale listing is invisible to the public until its waitlist window ends or the offer is declined. *(Done with spec 14: `ResaleApiTests.A_new_listing_is_the_oldest_matching_waiters_for_ten_minutes_and_public_after_their_window`.)*
- [x] Joining while the event still has tickets → `waitlist.not_available`.

## Implementation notes (what differs from the text above)

- **Source of returned tickets.** `Ticket.Void()` raises `TicketReturned(ticketId, eventId, tierId, seatIndex?, previousOwner)`, and the outbox delivers it to `TicketReturnedWaitlistHandler`. Nothing voids a paid ticket yet: admin refunds (16) only have to call `Void()`. Voiding for a cancelled event does not raise it. Resale (`ListingCreated`) waits for spec 14.
- **No `WaitlistOffer` table.** The offer lives on the entry (`offer_hold_id`, `offer_expires_at`, `misses`), and the offer id in the API is the entry id. One entry has at most one live offer.
- **Atomic hand-overs in Redis** (`waitlist_hold.lua`, not `hold_create.lua`):
  - `return` moves a sold seat or GA unit straight into the waiter's hold and lowers the previous owner's `bought`.
  - `pass` moves an expired offer's hold, as it is, to the next waiter.
  - `free` puts a ticket nobody waits for back on sale.
  - A returned seat is never free in between, and a seat held by someone else is refused (`TAKEN`). Waitlist holds carry `waitlist=1` and are not in the expiry ZSET, so the hold sweeper never frees them.
- **Matching:** the oldest `Waiting` entry (by `queued_at`, then id) whose tier is null or the same, and whose quantity fits the returned units. A returned ticket is one unit, so an entry for 2+ tickets waits for a pass of a larger hold. A waiter who already holds tickets for the event is skipped.
- **`WaitlistOfferService`** (worker, leader-elected) runs once a second and handles every offer past `offer_expires_at`:
  - It skips an offer while an order for its hold is still open: checkout's own expiry decides first.
  - The entry misses: the first miss sends it to the back of the line (`queued_at` = now), the second expires it.
  - The hold passes to the next matching waiter. When nobody matches, the hold is released to the public sale.
- **Crash safety:**
  - The first hold id is the ticket id. The id of a passed-on hold is derived from the old one (SHA-256).
  - A retry after a crash between Redis and the database save therefore finds the hold it already made and finishes that offer. It never leaves an orphaned hold, which nothing would sweep.
  - Database effects of the outbox handler commit with its consumer receipt.
- **Declining.** Releasing the offered hold (`DELETE /events/{id}/holds/me`, or cancelling its order) or leaving the waitlist ends the entry as `Left`. The hold goes to the next in line, never to the public first.
- **Cancelled event:** `EventCancelledWaitlistHandler` (the last `EventCancelled` consumer, after the holds are released) closes every live entry of the event (`Closed`).
- **Started event:** a ticket returned after the event's start is offered to nobody and goes straight back (free); an offer that runs out after the start is released, not passed on. Joining was already refused then.
- **Accept** returns `{holdId, eventId, eventSlug, expiresAt}` and **no admission token**: `POST /orders` is guarded by the user's live hold alone (08), and the waitlist already made that hold. The web then opens `/events/{slug}/checkout`.
- **Join rules:**
  - Confirmed email (403 `waitlist.email_not_confirmed`).
  - The event is published and has not started. It is sold out (warm inventory with nothing free; held counts as taken), or every phase has closed. Otherwise 409 `waitlist.not_available`.
  - One live entry per (event, user): 409 `waitlist.already_joined`, backed by a partial unique index.
  - Quantity 1–4: 400 `waitlist.invalid_quantity`.
  - Live tickets owned plus the quantity must not exceed the largest per-user phase cap: 409 `waitlist.limit_exceeded`.
- **Extra routes:**
  - `GET /me/waitlist` lists the caller's live entries with event info, for `/account/waitlist`.
  - `GET /waitlist/offers/{id}` returns the priced hold for the offer page: 404 `waitlist.offer_not_found` for someone else's offer, 409 `waitlist.offer_expired` once it is not live.
- **Notifications:** `WaitlistOfferIssued` produces the in-app notice "Có vé cho bạn!" and the `waitlist-offer` email. Both link to `/waitlist/offers/{id}`. The email expires with the offer, and the dedupe key is `waitlist-offer:{entry}:{hold}`.
- **Web:**
  - A sold-out event shows "Hết vé — Vào danh sách chờ". A visitor gets a login link. A signed-in fan gets a lazily loaded control: a Dialog with a tier select (including "Hạng bất kỳ"), a stepper for 1–4 tickets capped at the per-user limit, and how offers work. After joining, the button shows the place in line; "Rời danh sách chờ" leaves; a live offer becomes "Có vé cho bạn! Mua ngay".
  - The UI offers the waitlist only in the SoldOut state, though the api also accepts a closed sale.
  - `/account/waitlist` is in the account hub nav.
  - `/waitlist/offers/[id]` shows the event, a countdown, the priced lines and "Mua ngay", which leads to checkout.

