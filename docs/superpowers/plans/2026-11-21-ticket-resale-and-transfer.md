# Ticket Resale & Transfer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** resale at ≤ face value and transfer by email, with an atomic ownership change and a new QR (spec 14).

**Architecture:** new aggregates `ResaleListing`, `TicketTransfer`, `SellerPayout`; resale orders reuse 08 (`Order.Kind`,
`HoldId = listingId`); one ownership-change service used by fulfil and transfer accept; the waitlist gets a 10-minute
exclusive listing offer (approach A).

**Tech Stack:** .NET 10 Minimal API, EF Core/Npgsql, Redis, xUnit v3 + Shouldly + Testcontainers; Next 16, Vitest, Playwright.

**Spec:** `specs/14-ticket-resale-and-transfer.md`; design `docs/superpowers/specs/2026-11-21-ticket-resale-and-transfer-design.md`.

## Global Constraints

- Error codes exactly: `resale.not_allowed`, `resale.price_above_cap`, `resale.listing_unavailable`, `resale.own_listing`, `transfer.not_allowed`, `transfer.self`.
- Cutoff 2 h before `StartsAt`; transfer expiry 48 h; reservation `PX 600000`; waitlist window 10 min.
- Redis key `resale:{evt}:listing:<id>`; Redis `sold` never changes on resale.
- Never two non-voided tickets for one seat. Old ticket voided before the new one is inserted, same transaction.
- Ownership change never raises `TicketReturned`.
- UI copy in Vietnamese: *Bán lại*, *Chuyển cho bạn bè*, "Bạn nhận: …", "Vé hiện tại sẽ mất hiệu lực sau khi người nhận chấp nhận", "Vé bán lại", "Mua".
- Integration suite: `dotnet test tests/TicketRush.Api.IntegrationTests -c Release -- --max-threads 2`. Vitest from PowerShell.

## Review Focus

1. A late payment after the reservation expired and someone else reserved → refunded, never two tickets.
2. A transfer accepted while the same ticket's listing is being bought (impossible: one Listed reason at a time) — listing a ticket with a pending transfer → `resale.not_allowed`.
3. Event cancelled with a Sold listing → buyer and seller both refunded through 08, payout Cancelled (no double money).
4. Old outbox `OrderPaid` messages without `Kind` → treated as Primary.
5. Waitlist offer for a listing when the waiter already has an open hold/order elsewhere for the event → offer still made (they can buy one ticket).

---

### Task 1: Domain

**Files:** `src/TicketRush.Domain/Resale/{ResaleListing,TicketTransfer,SellerPayout,ResaleEvents}.cs`; modify `Tickets/Ticket.cs`,
`Orders/Order.cs`, `Orders/OrderDomainEvents.cs`, `Waitlist/WaitlistEntry.cs`. Tests in `tests/TicketRush.Domain.Tests/Resale/*`.

**Produces:** `Ticket.HandOver()`, `Ticket Ticket.Reissue(Guid orderId, Guid ownerUserId)`; `ResaleListing.Create(ticket, price, faceValue, now)`
(raises `ListingCreated`), `Reserve(orderId)`, `ReleaseReservation(orderId)`, `MarkSold()`, `Cancel()`, `OfferTo(userId)`,
`OpenToPublic(now)`, `IsOpenTo(userId, now)`; `TicketTransfer.Create(ticket, fromEmail, toEmail, now)` (raises `TransferCreated`),
`Accept(userId)`, `Cancel()`, `Expire()`; `SellerPayout.Create(listing)`, `MarkPaid(now)`, `Cancel()`; `OrderKind`,
`Order.CreateResale(...)`, `Order.Kind`, `Order.ListingId`; `OrderPaid.Kind`; `WaitlistEntry.OfferListing(listingId, expiresAt)`, `OfferListingId`.

- [ ] Tests first: price > face → `resale.price_above_cap`; price < 1 → `resale.invalid_price` (domain; the API validator answers 400 first); non-Valid ticket → `resale.not_allowed`; self transfer → `transfer.self`; HandOver raises no `TicketReturned`; Reissue keeps seat, new owner, new secret, Valid; listing state machine; transfer 48 h expiry; `IsOpenTo`.
- [ ] Implement; run `dotnet test tests/TicketRush.Domain.Tests`; commit.

### Task 2: Persistence

EF configurations, DbSets on `IAppDbContext`, migration `ResaleAndTransfer` (listings unique open listing per ticket, transfers
unique pending per ticket, payouts unique per listing, orders `kind`/`listing_id`). Commit with Task 3.

### Task 3: Listing endpoints + waitlist window

`POST /tickets/{id}/listings`, `DELETE /listings/{id}`, `GET /events/{id}/listings`; `ListingCreatedWaitlistHandler`;
expiry loop and leave handle listing offers; offer view returns the listing. Integration tests: create/cancel/list ordering,
hidden during window, offered to oldest matching waiter, opened when nobody waits, window end misses the entry once.

### Task 4: Reserve, pay, fulfil

`POST /listings/{id}/reserve` (idempotency), pay checks reservation, fulfil resale branch (ownership change + payout),
expiry/cancel release, `OrderPaid` consumers skip resale, waitlist converts by listing. Integration tests: acceptance 1, 2, 5;
late payment cases; own listing; unconfirmed email; cap.

### Task 5: Transfers

`POST /tickets/{id}/transfers`, `GET /transfers/{id}`, `POST /transfers/{id}/accept`, `DELETE /transfers/{id}`, recipient email
template. Integration tests: acceptance 4, email sent once, cancel restores Valid.

### Task 6: Sweeps and cancellation

Worker `ResaleSweepService` (transfer expiry, cutoff cancel, mock payouts), `EventCancelled` handler. Integration tests.

### Task 7: Views, OpenAPI, web types

Ticket view gains `listing`/`transfer`; regenerate OpenAPI snapshot and `web` types.

### Task 8: Web

Wallet menu, resale sheet, transfer dialog, event page "Vé bán lại", `/transfers/[id]`, waitlist offer page for listings.
Vitest + one Playwright spec with scripted endpoints. `scripts/ci-local.ps1` green, push.
