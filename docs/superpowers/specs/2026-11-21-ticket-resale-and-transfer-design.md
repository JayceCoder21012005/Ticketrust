# Ticket resale & transfer — design (spec 14)

Spec: `specs/14-ticket-resale-and-transfer.md`. Branch `feat/14-ticket-resale-and-transfer` (from spec 13).

## Intent

A fan who can't go resells on the platform at most at face value, or hands the ticket to a friend by email. Ownership
changes in one transaction; the old QR dies, the new owner gets a new one. The seat stays sold in Redis.

## What exists

- `Ticket` already has `Listed` with `List()` / `Unlist()`. The gate admits only `Valid` (`Scan.cs`) and the QR is shown only
  for `Valid` (`TicketViews`), so a listed ticket is already hidden and refused.
- `Ticket.Void()` raises `TicketReturned`, which feeds the waitlist (13). An ownership change must NOT raise it.
- Orders (08) are built around a Redis hold (`HoldId`): pay checks the hold, fulfil claims inventory, `OrderPaid` commits it
  to Redis and leaves the queue, expiry/cancel release the hold.
- The waitlist (13) offers returned holds; `WaitlistEntry` has only `OfferHoldId`.

## Approaches (waitlist window) — picked A

- **A (picked):** a new listing goes to the oldest matching waiter for 10 minutes; unused, it becomes public at once (no
  pass-on). The seller waits 10 minutes at most.
- B: pass it down the line like a returned ticket. Fairer to waiters, but a listing can stay hidden for a long time.
- C: every waiter may buy during the 10 minutes, first come. Simple, but ignores the line.

## Design

**Domain**
- `ResaleListing(ticketId, sellerId, eventId, tierId, seatIndex?, price, status, waitlistUntil?, offeredToUserId?, reservedOrderId?)`.
  `Active → Reserved → Sold`, `Reserved → Active` (reservation ended), `Active → Cancelled`.
- `TicketTransfer(ticketId, fromUserId, toEmail, toUserId?, status, expiresAt = created + 48 h)`. `Pending → Accepted | Cancelled | Expired`.
- `SellerPayout(listingId, sellerId, eventId, amount, status Pending → Paid | Cancelled)`.
- `Order.Kind` (`Primary` | `Resale`) and `Order.ListingId`. A resale order's `HoldId` is the listing id, so the existing
  one-open-order-per-hold index also means one open order per listing. `OrderPaid` carries `Kind` (old messages default to Primary).
- `Ticket.HandOver()`: Listed → Voided without `TicketReturned`; `Ticket.Reissue(orderId, newOwner)` copies event, tier, seat
  with a new `QrSecret`.

**Ownership change** (resale fulfil and transfer accept), in one transaction: a conditional set-based UPDATE voids the old
ticket (`status = Listed AND owner = seller`; 0 rows → conflict), then the new ticket is inserted. Voiding first keeps the
partial unique seat index satisfied. Resale: the new ticket's order is the resale order. Transfer: the original order.

**Resale purchase**
- Reserve (`POST /listings/{id}/reserve`, idempotent): needs a confirmed email, not the seller, inside the sale window, the
  waitlist window open to this user (`waitlistUntil` passed, or `offeredToUserId` is the buyer), and the per-user cap. Redis
  `SET resale:{evt}:listing:<id> <buyer> NX PX 600000`, then `UPDATE … SET status='Reserved', reserved_order_id=@o WHERE
  id=@id AND status='Active'` in the same transaction as the order insert. Either fails → `resale.listing_unavailable` (the
  Redis key is deleted if the DB lost). The same buyer asking again gets their open order back.
- Pay reuses 08 and checks the reservation instead of the hold.
- Fulfil (webhook): sale lock, per-user cap, `UPDATE … SET status='Sold' WHERE id=@id AND (status='Reserved' AND
  reserved_order_id=@order OR status='Active')` (a late payment still sells a listing nobody took), the ownership change,
  a Pending payout. 0 rows → the existing unfulfillable path refunds (`seats_unavailable`).
- Expiry/cancel of a resale order: the listing goes back to Active (if still reserved by that order) and the Redis key is
  deleted (only if it is the buyer's). Redis `sold` is never touched; `OrderPaid` inventory commit and queue leave skip resale.

**Waitlist (A):** a listing is created with `waitlistUntil = now + 10 min`. `ListingCreated` → handler picks the oldest
Waiting entry wanting that tier (quantity 1): `entry.OfferListing(listingId, waitlistUntil)` and `listing.OfferTo(user)`;
nobody → `listing.OpenToPublic()`. The offer page shows the listing; "Mua ngay" reserves it and goes to `/checkout/{orderId}`.
When the window ends unused the expiry loop misses the entry once (as in 13) and nothing else moves. Leaving the waitlist with
a live listing offer opens the listing at once. Paying converts the entry.

**Rules and sweeps**
- List/transfer only `Valid` tickets of a Published event until 2 h before start; price 1…face value (the tier price, fixed
  after publish). Reserve and accept are refused after that cutoff too.
- A worker sweep (leader): pending transfers past 48 h → Expired; at the cutoff, Active listings → Cancelled and pending
  transfers → Cancelled (ticket back to Valid so the owner can still go); Pending payouts of Ended events → Paid (mock).
- Event cancelled: listings and transfers cancel, Pending payouts cancel (the seller's original order is refunded by 08).
- Transfer to your own email → `transfer.self`. Accept: signed in, email confirmed and equal (case-insensitive) to `toEmail`,
  else 404. Accepting an Accepted transfer as its recipient returns it again (idempotent).
- The recipient gets an email (09) with `/transfers/{id}`.

**API** as in the spec, plus `GET /transfers/{id}` (recipient view for the accept page) and the ticket view gains its live
listing/transfer so the wallet can cancel it. Errors: `resale.not_allowed`, `resale.price_above_cap`,
`resale.listing_unavailable`, `resale.own_listing`, `transfer.not_allowed`, `transfer.self`.

**Web**: wallet "…" menu (Bán lại / Chuyển cho bạn bè, or cancel while listed), resale `Sheet` (price + slider capped at face
value, "Bạn nhận", rules), transfer `Dialog` with the warning, "Vé bán lại" section on a sold-out event page, `/transfers/[id]`
accept page (login first, then back), waitlist offer page for listing offers.

## Left to the builder

Table/column names, the sweep interval, copy wording beyond the spec's, the shape of view models.
