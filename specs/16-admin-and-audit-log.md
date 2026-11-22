# 16 — Admin & Audit Log

**Goal:** admins operate the platform safely (users, organizers, events, orders, failed jobs), and every sensitive action leaves a record that can't be edited.

**Depends on:** 03 (uses data from every spec, so build it after 15 for full coverage)

## Audit log

`audit_entries(id, at, actor_user_id, actor_ip, action, entity_type, entity_id, changes jsonb, correlation_id)` — **append-only**. The app's DB role has `INSERT, SELECT` only on this table, never `UPDATE`/`DELETE`.

- **Automatic**: an EF `SaveChanges` interceptor records create/update/delete on entities marked `[Audited]` (Event, TicketTier, SalePhase, Order status, Ticket status, User roles/lockout). `changes` holds field-level before/after values. Fields marked `[Sensitive]` (password hash, QR secret, tokens) are never written.
- **Explicit**: `IAuditLogger.LogAsync(action, …)` for security events: login failed / locked out, refresh token reuse detected, role granted / revoked, admin refund, impersonation is **not** supported.

## Admin features (policy `RequireAdmin`)

| Area | Capabilities |
|---|---|
| Users | Search (email, name), view profile + orders, lock/unlock, grant/revoke roles. An admin can't remove their own Admin role |
| Organizer applications | Users apply (`POST /organizer-applications {companyName, phone, description}`). The admin approves → `Organizer` role + email, or rejects with a reason |
| Events | List all, unpublish (moderation) with a reason, which notifies the organizer |
| Orders | Look up by id/email/phone. **Manual refund**: order → `RefundPending` (08 path), tickets voided → `InventoryReturned` (13). Requires a reason, double confirmation |
| Outbox | Dead-letter list with the error. Actions: retry, discard (with a reason) |
| Risk flags | Accounts flagged by 12 (`SuspiciousActivityDetector`): review, dismiss, or lock |
| Audit | Filter by actor, entity, action, date. Detail drawer shows the before/after diff |

API routes live under `/admin/*`, keyset pagination everywhere. Each mutation requires a `reason` field that goes into the audit entry.

## Frontend `/admin`

- Same shell as the organizer area (sidebar + breadcrumb), with an admin-only accent colour so admins always know where they are.
- Data tables: server-side filtering and sorting, column visibility, row actions menu, URL-synced filters.
- `Command` palette (⌘K): jump to user/order/event by id or email.
- Destructive actions: `AlertDialog` that requires the reason text and confirms the target name.
- Audit diff viewer: two-column before/after with changed fields highlighted.

## Acceptance criteria

- [x] An `UPDATE` on `audit_entries` fails at the database level.
- [x] Changing a user's role creates exactly one audit entry with actor, IP and correlation id.
- [x] Audit entries never contain password hashes, tokens or QR secrets (test scans `changes`).
- [x] A manual refund releases the seat to the waitlist (13) and notifies the buyer.
- [x] Non-admins get 404 for every `/admin` route (API and pages).
- [x] Retrying a dead-letter message processes it once.

## Implementation notes (what differs from the text above)

- **Unpublish = suspend.** `EventStatus.Suspended`: hidden from the catalog and closed to holds, orders, resale and transfers (everything that requires `Published`), while sold tickets stay valid, show their QR and pass the gate. The admin restores it to Published; tiers stay frozen. The organizer gets an in-app notice and an email with the reason both ways. Suspending takes the event sale lock exclusively, like a cancellation.
- **Append-only.** A trigger rejects UPDATE, DELETE and TRUNCATE on `audit_entries` for every role (the owner included), and those privileges are revoked from `PUBLIC`. A separate INSERT/SELECT-only app role is a deployment step.
- **Automatic entries** are written in the same save as the change, field by field. Order and Ticket record status changes only (not their bulk creation); `AppUser` records lockout only. Set-based updates (the cancelled-event settlement, the gate's conditional check-in) bypass the interceptor and are not in the log.
- **Roles** are recorded explicitly (`role.granted` / `role.revoked`, with the reason), never automatically, so one change is exactly one entry.
- **Manual refund** works on paid primary orders whose tickets were not used; it is refused while one of the tickets is being bought on resale. Double confirmation = typing the order id. The buyer is told by 08's `RefundRequested` notice (reason `admin_refund`).
- **Dead letters.** Retry puts the message back with fresh attempts; consumer receipts keep every handler at most once. Discard keeps it with the reason and drops it from the list.
- **Locking** an account also revokes all of its refresh tokens.
- **Users apply** at `/account/organizer`. One pending application per user.
- **Sorting.** Admin lists are newest first with keyset pages; filters are server-side and synced to the URL. There are no per-column sort controls.
