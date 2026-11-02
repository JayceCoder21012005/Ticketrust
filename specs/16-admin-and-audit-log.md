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

- [ ] An `UPDATE` on `audit_entries` fails at the database level.
- [ ] Changing a user's role creates exactly one audit entry with actor, IP and correlation id.
- [ ] Audit entries never contain password hashes, tokens or QR secrets (test scans `changes`).
- [ ] A manual refund releases the seat to the waitlist (13) and notifies the buyer.
- [ ] Non-admins get 404 for every `/admin` route (API and pages).
- [ ] Retrying a dead-letter message processes it once.
