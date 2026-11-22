# Admin & audit log — design (spec 16)

Spec: `specs/16-admin-and-audit-log.md`. Branch `feat/16-admin-and-audit-log` (from spec 15).

## Intent

Admins run the platform (users, organizers, events, orders, failed jobs, risk flags) and every sensitive change leaves a record
nobody can edit.

## Decisions

- **Unpublish = suspend (user's choice).** New `EventStatus.Suspended`: hidden from the catalog, no holds, orders, resale or
  transfers (everything that checks `Published` refuses it), but sold tickets stay valid, show their QR and pass the gate. The
  admin can restore it to Published. The organizer gets an email and an in-app notice with the reason, both ways. Tiers stay
  frozen (no Draft round trip), so 08's fixed-price rule holds.
- **Append-only at the database.** A trigger `BEFORE UPDATE OR DELETE OR TRUNCATE` on `audit_entries` raises, whatever role runs
  it, and `REVOKE UPDATE, DELETE, TRUNCATE` from `PUBLIC`. (A separate app role with only INSERT/SELECT is a deployment step; the
  trigger makes the guarantee hold for the owner role too.)
- **Automatic entries** come from a `SaveChanges` interceptor in the same transaction as the change. `[Audited]` on Event,
  TicketTier, SalePhase; `[Audited(nameof(Status), …)]` limits Order and Ticket to their status fields; `AppUser` is audited for
  lockout only. `[Sensitive]` properties (QR secret, password hash, security stamp, token hashes) are never written; a test scans
  every entry. Field-level before/after JSON; complex properties flattened (`total.amount`).
- **Explicit entries** through `IAuditLogger`: `auth.login_failed`, `auth.locked_out`, `auth.refresh_reuse`, `role.granted`,
  `role.revoked` (role rows are not auto-audited, so a role change is exactly one entry), `order.refund_manual`,
  `event.suspended`/`event.restored`, `outbox.retried`/`outbox.discarded`, `flag.dismissed`/`flag.locked`,
  `organizer_application.approved`/`rejected`, `user.locked`/`user.unlocked`.
- **Actor, IP, correlation** from the HTTP request (`CorrelationIdMiddleware`); worker writes have no actor (`system`).
- **404 for non-admins** on every `/admin` route: an authorization result handler turns the admin policy's 401/403 into 404 for
  `/api/v1/admin`; the web admin area checks the role and renders `notFound()`.
- **Manual refund:** Paid → RefundPending (08's refund worker pays it back), every ticket of the order voided with `Ticket.Void()`
  (13's `TicketReturned`, so the waitlist gets the seat), listings/transfers of those tickets closed; the buyer is notified by 08's
  `RefundRequested` notice. Double confirmation = the request must repeat the order code. Resale orders are refused (scope).
- **Dead letters:** retry resets the message (attempts 0, due now, error kept until it succeeds); consumer receipts make a retry
  run each handler at most once. Discard marks it processed with the reason.
- **Organizer applications:** one open application per user; approve grants `Organizer` and emails; reject needs a reason, emails.
- **Pagination:** keyset (created/at, id) everywhere; every mutation body has `reason` (1–500 chars).

## Web

`/admin` with the organizer shell and an admin accent; pages Users, Applications, Events, Orders, Outbox, Flags, Audit. Tables with
server filters synced to the URL, a row action menu and column visibility; ⌘K palette (user/order/event by id or email);
destructive actions in an `AlertDialog` asking for the reason and the target's name; audit drawer with a two-column diff.

## Left to the builder

Exact copy, column sets, page sizes.
