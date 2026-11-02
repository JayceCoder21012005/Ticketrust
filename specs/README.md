# TicketRush — Build Specs

TicketRush is a flash-sale ticketing platform: tens of thousands of fans hit "Buy" at the same second, and the system must stay fair, fast and **never sell the same seat twice**.

These specs are written for a coding agent. Build them **in order**, one spec per session/PR. Never start a spec before its dependencies are merged and green.

## How to use with a coding agent

1. Always load `00-architecture-and-conventions.md` + the spec you are building. Load `02-frontend-design-system.md` for any UI work.
2. Prompt: *"Implement `specs/NN-name.md`. Follow 00 conventions. Stop and list open questions instead of guessing."*
3. A spec is **done** only when every Acceptance Criteria box is checked and its tests pass in CI.
4. If a spec conflicts with 00, 00 wins. If a spec is ambiguous, update the spec first, then code.

## Build order

| # | Spec | Phase | Depends on |
|---|------|-------|-----------|
| 00 | [Architecture & conventions](00-architecture-and-conventions.md) | — | — |
| 01 | [Project setup & infrastructure](01-project-setup-and-infrastructure.md) | MVP | 00 |
| 02 | [Frontend design system](02-frontend-design-system.md) | MVP | 01 |
| 03 | [Auth & user accounts](03-auth-and-user-accounts.md) | MVP | 01, 02 |
| 04 | [Event catalog & search](04-event-catalog-and-search.md) | MVP | 03 |
| 05 | [Organizer event management](05-organizer-event-management.md) | MVP | 04 |
| 06 | [Virtual waiting room](06-virtual-waiting-room.md) | MVP | 04 |
| 07 | [Seat selection & hold](07-seat-selection-and-hold.md) | MVP | 06 |
| 08 | [Checkout & payment](08-checkout-and-payment.md) | MVP | 07 |
| 09 | [Notifications & email](09-notifications-and-email.md) | MVP | 08 |
| 10 | [E-tickets & rotating QR](10-e-tickets-and-rotating-qr.md) | MVP | 08, 09 |
| 11 | [Gate check-in](11-gate-check-in.md) | V2 | 10 |
| 12 | [Anti-bot & rate limiting](12-anti-bot-and-rate-limiting.md) | V2 | 06, 07 |
| 13 | [Sold-out waitlist](13-sold-out-waitlist.md) | V2 | 08, 09 |
| 14 | [Ticket resale & transfer](14-ticket-resale-and-transfer.md) | V2 | 10 |
| 15 | [Organizer realtime dashboard](15-organizer-realtime-dashboard.md) | V2 | 08 |
| 16 | [Admin & audit log](16-admin-and-audit-log.md) | V2 | 03 |
| 17 | [Load testing & observability](17-load-testing-and-observability.md) | Bonus | 08 |

## The one-paragraph architecture

Next.js (App Router + shadcn/ui) talks to an ASP.NET Core 10 API. PostgreSQL is the **source of truth** for orders and tickets. Redis is the **fast path** for the waiting room, seat holds and inventory counters, executed as atomic Lua scripts. A separate Worker process runs background jobs (outbox, hold expiry, queue admission, payment reconciliation). SignalR (Redis backplane) pushes seat and dashboard updates. Everything is orchestrated locally by Aspire.
