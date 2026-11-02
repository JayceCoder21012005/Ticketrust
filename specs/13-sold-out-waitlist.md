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

- [ ] With 3 waiting users and 1 returned ticket, only the oldest matching user gets an offer.
- [ ] An expired offer passes to the next user within 5 s, and the ticket is never offered to two users at once.
- [ ] A resale listing is invisible to the public until its waitlist window ends or the offer is declined.
- [ ] Joining while the event still has tickets → `waitlist.not_available`.
