# 14 — Ticket Resale & Transfer

**Goal:** fans who can't attend can resell on the platform (price capped at face value, which kills scalping) or transfer to a friend. The old QR dies the moment ownership changes.

**Depends on:** 10 (13 for the waitlist window)

## Domain

- `ResaleListing(ticketId, sellerId, eventId, tierId, price: Money, status, waitlistUntil?)`. Status: `Active → Reserved → Sold`, `Active → Cancelled`.
- `TicketTransfer(ticketId, fromUserId, toEmail, toUserId?, status, expiresAt)`. Status: `Pending → Accepted | Cancelled | Expired` (48 h).
- `SellerPayout(listingId, sellerId, amount, status Pending → Paid)`. Paid by a mock job after the event ends. Real bank payout is out of scope.
- `Order.Kind` = `Primary` | `Resale` (+ `ListingId`). The resale checkout reuses 08 entirely.
- While listed or pending transfer, the ticket status is `Listed`: QR hidden (10), check-in refused (11).

## Rules

- Allowed only for `Valid` tickets, until 2 h before the event starts. Price ≤ original face value (`resale.price_above_cap`).
- **Ownership change** (resale paid or transfer accepted) in one transaction: old ticket → `Voided`, new `Ticket` for the new owner with the same seat and a **new `QrSecret`**. The partial unique index from 08 stays satisfied because the old row is voided first in the same transaction.
- Resale purchase: buyer needs a confirmed email. The per-user cap (12) applies, no queue.
  - Reserve: Redis `SET resale:{evt}:listing:<id> <buyerId> NX PX 600000` + `UPDATE resale_listings SET status='Reserved' WHERE id=@id AND status='Active'`. Either failing → `resale.listing_unavailable`.
  - Fulfil (on `OrderPaid`): `UPDATE … SET status='Sold' WHERE id=@id AND status='Reserved'`, then the ownership change. Payout created.
  - Reservation expires → back to `Active`.
- New listing → `ListingCreated` → the waitlist gets an exclusive 10-min window (`waitlistUntil`, 13). Public listing endpoints hide it until then.
- Sellers can't buy their own listing. A transfer to your own email is rejected.
- Redis `sold` is unchanged (the seat stays sold); only the owner changes.

## API

| Method | Route | Auth | Notes |
|---|---|---|---|
| POST | `/tickets/{id}/listings` | owner | `{price}` |
| DELETE | `/listings/{id}` | seller | Only when `Active` |
| GET | `/events/{id}/listings` | public | Active, past the waitlist window. Grouped by tier, price ascending |
| POST | `/listings/{id}/reserve` | user + idempotency | → order (`Kind=Resale`) → pay via 08 |
| POST | `/tickets/{id}/transfers` | owner | `{toEmail}`. Emails the recipient (09) |
| POST | `/transfers/{id}/accept` | recipient (email must match) | Ownership change |
| DELETE | `/transfers/{id}` | sender | Cancel while `Pending` |

Error codes: `resale.not_allowed`, `resale.price_above_cap`, `resale.listing_unavailable`, `resale.own_listing`, `transfer.not_allowed`, `transfer.self`.

## Frontend

- Ticket wallet (10) gets a "…" menu: *Bán lại*, *Chuyển cho bạn bè*.
- Resale `Sheet`: price input with a slider capped at face value, showing "Bạn nhận: …", rules summary, confirm.
- Transfer `Dialog`: recipient email, warning "Vé hiện tại sẽ mất hiệu lực sau khi người nhận chấp nhận".
- Event page shows a "Vé bán lại" section when sold out and listings exist: list by tier with price and a "Mua" button.
- `/transfers/[id]` accept page for recipients (login/register first, then return here).

## Acceptance criteria

- [x] After a resale, the seller's old QR is rejected at the gate (`NotValid`). The buyer's new QR is admitted.
- [x] Two buyers reserving the same listing at the same time → exactly one succeeds.
- [x] Listing above face value → 400. Listing a checked-in ticket → `resale.not_allowed`.
- [x] A transfer accepted by a different email → 403-as-404. Accepting twice → idempotent.
- [x] At no time do two non-voided tickets exist for one seat (invariant check after a concurrent resale + transfer test).

## Implementation notes (what differs from the text above)

- **Waitlist window (approach A).** A new listing is offered to the oldest Waiting entry that wants one ticket of its tier, alone, for 10 minutes (`waitlistUntil`). Unused, it becomes public at once: nothing passes down the line (unlike a returned ticket in 13). With nobody waiting it is public immediately. The offer page reserves the listing and goes to `/checkout/{orderId}`.
- **Resale orders reuse 08.** `Order.Kind = Resale`, `ListingId` set, and `HoldId` is the listing id, so the one-open-order-per-hold index also means one open order per listing. Pay checks the listing's reservation instead of a Redis hold; expiry and cancel put the listing back to `Active` and free the Redis key; the `OrderPaid` inventory commit and queue leave skip resale orders (`OrderPaid.Kind`, old messages read as Primary).
- **Late payment.** Fulfil sells a listing still reserved by that order, or still `Active` after the reservation ran out. Anything else (someone else reserved or bought it) is refunded as `seats_unavailable`.
- **Ownership change.** `Ticket.HandOver()` (Listed → Voided, raises no `TicketReturned`) is saved before `Ticket.Reissue(...)` is added, in one transaction. A resale ticket's order is the resale order; a transferred ticket keeps the original order.
- **Cutoff and sweeps.** Reserve and accept are refused inside the 2-hour cutoff too. A worker sweep (30 s, leader) expires 48-hour transfers, ends active listings and pending transfers at the cutoff (the owner keeps a usable ticket), and pays out pending payouts of ended events (mock). A cancelled event ends listings and transfers and cancels pending payouts (the seller's original order is refunded by 08).
- **Extra endpoint.** `GET /transfers/{id}` for the accept page. The ticket view (`GET /tickets/{id}`) carries `resale` (face value, whether it can change hands, the live listing or transfer) for the wallet menu.
- **Recipient email.** The transfer email (`ticket-transfer` template) goes straight to the address; there is no in-app notice, since the recipient may have no account yet.
