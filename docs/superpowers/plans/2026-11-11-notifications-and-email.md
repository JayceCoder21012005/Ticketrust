# Notifications & Email Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every order, refund, cancellation and reminder event reaches the user in the app and by email exactly once, and an SMTP outage never blocks other notifications (spec 09).

**Architecture:** Outbox handlers write a `notifications` row and an `email_deliveries` row in one transaction through `INotificationWriter` (unique `dedupe_key`); a worker sends the emails with retry; `/hubs/user` pushes live; a leader-elected scheduler emits reminders. Web adds a bell, a list page, toasts and a follow button.

**Tech Stack:** .NET 10, EF Core/Npgsql, Scriban, MailKit, SignalR + Redis backplane, Testcontainers (Postgres, Redis, Mailpit), Next.js 16, TanStack Query, sonner, Vitest, Playwright.

**Spec:** `specs/09-notifications-and-email.md` (binding). Design and decisions: `docs/superpowers/specs/2026-11-11-notifications-and-email-design.md` (read its "Data" and "Trigger mapping" tables first).

**Branching:** `feat/09-notifications-and-email` cut from `main`. Nothing is pushed or merged by the agent.

## Global Constraints

- Spec 00 rules: sealed, primary constructors, `TimeProvider`, `CancellationToken ct` everywhere, methods ≤ 30 lines, `TreatWarningsAsErrors`, Application references no provider (raw SQL / Npgsql only in Infrastructure).
- Vietnamese UI and email copy only; display time in `Asia/Ho_Chi_Minh`. Emails: one shared `_layout.html`, inline CSS, `.html` + `.txt` pair per template, no remote image except the logo, every value HTML-escaped.
- Reset link expires in 1 h; confirm link 3 days (existing providers). Order email has the "Xem vé" link and **no QR**.
- Never log email addresses, tokens or message bodies.
- Error codes: `notification.not_found` (404), `event.not_followable` (409).
- List endpoints: keyset `?cursor=&limit=` default 20, max 100, `{ items, nextCursor }`.
- TDD per task. Baselines that must stay green: Domain 285, Architecture 15, Application 152, Api.IntegrationTests 709 (run with `-- --max-threads 2`), Vitest 755, Playwright chromium 135.
- No `git add -f` under `.superpowers/`, never stage `web/package-lock.json` unless the task adds a dependency there, no `git stash`.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review Focus

1. Same `OrderPaid` message delivered twice, and the same order emitted as a second message → still one notification, one email row (T4).
2. SMTP down: the handler still commits, in-app row exists, `EmailDeliveryService` retries with backoff and later sends once; a second SMTP outage after a partial send does not duplicate (T3).
3. Reminder scheduler on two instances, and a lock lost mid-tick → one `ReminderDue` per (user, event, kind) (T8).
4. `EventCancelled` with 10 000 followers/holders: no per-user round trips, no duplicate on redelivery, a user who both follows and holds gets one notification (T5).
5. User with no/unconfirmed email, deleted user, user opening two tabs: no crash, no row for a missing user, both tabs get the push (T3, T6).

---

### Task 1: Persistence — notifications, deliveries, follows, reminder dispatches

**Files:**
- Create: `Domain/Notifications/{Notification,NotificationType,EventFollow,ReminderDispatch,ReminderKind,NotificationDomainEvents}.cs`, `Domain/Notifications/EmailDelivery.cs`, `Infrastructure/Persistence/Configurations/{Notification,EmailDelivery,EventFollow,ReminderDispatch}Configuration.cs`, migration `AddNotifications`
- Modify: `Application/Abstractions/Persistence/IAppDbContext.cs`, `Infrastructure/Persistence/AppDbContext.cs`
- Test: `tests/TicketRush.Domain.Tests/Notifications/*`, `tests/TicketRush.Api.IntegrationTests/Notifications/NotificationSchemaTests.cs`

**Interfaces:**
- Produces: `Notification.Create(Guid userId, NotificationType type, string title, string body, string? link, string dedupeKey, DateTimeOffset now)`, `Notification.MarkRead(DateTimeOffset now)` (idempotent: keeps the first `ReadAt`); `NotificationType` = `OrderPaid, RefundRequested, Refunded, OrderExpired, EventCancelled, SaleOpensSoon, EventStartsSoon`; `ReminderKind` = `SaleOpens30m, EventStarts24h`; `ReminderDispatch.Create(Guid userId, Guid eventId, ReminderKind kind, DateTimeOffset now)` raises `ReminderDue(UserId, EventId, Kind)` (`IDomainEvent`); `EmailDelivery.Create(Guid userId, string toAddress, string template, string modelJson, string dedupeKey, DateTimeOffset now)`, `RecordFailure(DateTimeOffset now, string error, double random)` (uses `OutboxRetryPolicy`; returns true when dead-lettered), `MarkSent(DateTimeOffset now)`; `EventFollow.Create(Guid userId, Guid eventId, DateTimeOffset now)`. `IAppDbContext` gains `Notifications`, `EmailDeliveries`, `EventFollows`, `ReminderDispatches`.

- [ ] **Step 1: Failing domain tests** — `Notification_create_trims_and_rejects_blank_title`, `Notification_mark_read_keeps_first_read_at`, `EmailDelivery_failure_schedules_backoff_and_dead_letters_after_max_attempts`, `ReminderDispatch_create_raises_ReminderDue`.
- [ ] **Step 2: Run** `dotnet test tests/TicketRush.Domain.Tests` → FAIL (types missing).
- [ ] **Step 3: Implement** the entities above (`ReminderDispatch` is an `AggregateRoot`, the rest `Entity`; no public setters; `ReminderDue` registered through the existing assembly scan in `DomainEventTypeRegistry`).
- [ ] **Step 4: Failing integration test** `NotificationSchemaTests`: `Duplicate_notification_dedupe_key_is_a_unique_violation`, `Duplicate_follow_is_a_unique_violation`, `Duplicate_reminder_dispatch_is_a_unique_violation`, `Keyset_index_exists` (query `pg_indexes`). Run → FAIL.
- [ ] **Step 5: Configurations + migration** exactly as the design's "Data" table (snake_case like existing configs, `timestamptz`, `xmin` not needed). Generate with `dotnet ef migrations add AddNotifications` using the existing design-time factory. Run domain + schema tests → PASS.
- [ ] **Step 6: Commit** `feat(09): notification, delivery, follow and reminder tables`

---

### Task 2: Email rendering — Scriban templates and layout

**Files:**
- Create: `Application/Abstractions/Notifications/IEmailRenderer.cs`, `Infrastructure/Email/ScribanEmailRenderer.cs`, `Infrastructure/Email/Templates/_layout.html`, `Templates/{confirm-email,reset-password,order-paid,refund-requested,refunded,event-cancelled,reminder-sale-opens,reminder-event-starts}.{html,txt}` (embedded resources), `web/public/logo-email.png`
- Modify: `Directory.Packages.props` (Scriban), `Infrastructure/Identity/AccountEmails.cs`, `Infrastructure/DependencyInjection.cs`
- Test: `tests/TicketRush.Api.IntegrationTests/Notifications/EmailRenderingTests.cs`

**Interfaces:**
- Produces: `IEmailRenderer.Render(string template, object model) : RenderedEmail(string Subject, string Html, string Text)`. The subject is a `{{ subject }}` block at the top of each `.txt`'s front matter line `Subject: …`. Models are anonymous/record objects with camelCase members (`name`, `eventTitle`, `orderCode`, `amount`, `link`, `startsAtText`).
- Consumes: `FrontendOptions.BaseUrl` for the logo and links.

- [ ] **Step 1: Failing tests** `EmailRenderingTests`: `Every_template_renders_html_and_text_with_vietnamese_diacritics` (theory over the 8 template names; asserts `"Xin chào Nguyễn Văn Đạt"` appears in both parts and no `{{` remains), `Html_values_are_escaped` (name `<script>` → `&lt;script&gt;`), `Html_has_no_remote_image_except_the_logo` (regex over `src=`), `Order_paid_has_xem_ve_link_and_no_qr` (contains `/account/orders/{id}`, does not contain `qr`, case-insensitive), `Layout_is_shared` (all html outputs contain the layout footer marker), `Reset_email_states_one_hour`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** `ScribanEmailRenderer` (templates loaded once from embedded resources, `TemplateContext` with `MemberRenamer = member => camelCase`, HTML parts rendered with `html.escape` on every interpolation, layout injected as `{{ body }}`); register `IEmailRenderer` as singleton; add the Scriban package version per design call 7.
- [ ] **Step 4: Convert account emails**: `SendConfirmationEmailHandler` and `SendPasswordResetEmailHandler` render `confirm-email` / `reset-password` and drop `AccountLinks.Email`. Keep their existing tests green (`CapturingEmailSender` assertions on subject/link).
- [ ] **Step 5: Run** the new tests and the existing auth email tests → PASS.
- [ ] **Step 6: Commit** `feat(09): scriban email templates with shared layout`

---

### Task 3: Notification writer and email delivery worker

**Files:**
- Create: `Application/Abstractions/Notifications/{INotificationWriter,IUserDirectory,IUserNotifier}.cs`, `Infrastructure/Notifications/{NotificationWriter,UserDirectory}.cs`, `Worker/Notifications/EmailDeliveryService.cs`, `Infrastructure/Notifications/EmailDeliveryProcessor.cs`
- Modify: `Infrastructure/DependencyInjection.cs`, `Worker/Program.cs`, `Infrastructure/Options/InfrastructureOptions.cs` (`EmailDeliveryOptions`: `BatchSize` 20, `PollInterval` 2 s)
- Test: `tests/TicketRush.Api.IntegrationTests/Notifications/{NotificationWriterTests,EmailDeliveryTests}.cs`

**Interfaces:**
- Produces: `record NotificationDraft(Guid UserId, NotificationType Type, string Title, string Body, string? Link, string DedupeKey, EmailPlan? Email)`; `record EmailPlan(string Template, object Model)`; `INotificationWriter.AddAsync(NotificationDraft draft, CancellationToken ct) : Task<Guid?>` — returns the notification id, or `null` when the key already existed (then **nothing** is pushed or queued). `IUserDirectory.GetContactAsync(Guid userId, CancellationToken ct) : Task<UserContact?>` (`record UserContact(string Email, string FullName, bool EmailConfirmed)`; `null` for a missing user). `IUserNotifier.PushAsync(Guid userId, NotificationItem item, CancellationToken ct)` and `PushOrderStatusAsync(Guid userId, Guid orderId, string status, CancellationToken ct)` (implemented in T6).
- Writer rule: email is queued only when the contact exists, has an email address, and `EmailConfirmed`; otherwise only the in-app row is written. The writer inserts both rows with `SaveChangesAsync` on the handler's scoped `IAppDbContext` (joins the outbox transaction) and treats a unique violation on `dedupe_key` as "already done".

- [ ] **Step 1: Failing tests** `NotificationWriterTests`: `Same_dedupe_key_twice_writes_one_notification_and_one_delivery`, `Unconfirmed_email_gets_in_app_only`, `Missing_user_writes_nothing`. `EmailDeliveryTests`: `Delivery_is_sent_once_and_marked_sent` (CapturingEmailSender), `Smtp_failure_reschedules_with_backoff_and_does_not_touch_other_deliveries` (sender throws for one address, other delivery in the same batch is sent), `Dead_letters_after_max_attempts`, `Two_processors_never_send_the_same_row` (parallel `ProcessBatchAsync`, `FOR UPDATE SKIP LOCKED`), `Sent_email_uses_delivery_id_as_message_id`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** `NotificationWriter`, `UserDirectory` (over `UserManager`/the identity tables), `EmailDeliveryProcessor.ProcessBatchAsync(ct) : Task<int>` (claim pending rows due now with `FOR UPDATE SKIP LOCKED` in a transaction exactly like `OutboxProcessor.ClaimBatchAsync`; render via `IEmailRenderer`; send each with its own try/catch so one failure only reschedules that row), and `EmailDeliveryService` (a `BackgroundService` loop like `OutboxDispatcherService`, no leader needed). Register in Worker.
- [ ] **Step 4: Run** → PASS. Also run `ArchitectureTests` (Application must not reference Npgsql).
- [ ] **Step 5: Commit** `feat(09): notification writer and email delivery worker`

---

### Task 4: Order, refund and expiry notification handlers

**Files:**
- Create: `Application/Features/Notifications/OrderNotificationHandlers.cs` (one class per event: `OrderPaidNotificationHandler`, `RefundRequestedNotificationHandler`, `OrderRefundedNotificationHandler`, `PaymentRefundedNotificationHandler`, `OrderExpiredNotificationHandler`, each `IIntegrationEventHandler<TEvent>`), `Application/Features/Notifications/NotificationCopy.cs` (Vietnamese titles/bodies)
- Modify: `Infrastructure/DependencyInjection.cs` (register each with `AddIntegrationEventHandler`; these handlers are registered **before** any other `OrderPaid` handler so in-app work is never behind a failing one)
- Test: `tests/TicketRush.Api.IntegrationTests/Notifications/OrderNotificationTests.cs`

**Interfaces:**
- Consumes: `INotificationWriter`, `IUserNotifier.PushOrderStatusAsync`, `IAppDbContext` (event title, order code, amount).
- Dedupe keys and mapping exactly as the design doc. `PaymentRefunded` resolves its user through `OrderId`.

- [ ] **Step 1: Failing tests**: `Replaying_the_same_OrderPaid_message_creates_one_notification_and_one_email_delivery` (call the outbox processor twice on one message **and** insert a second outbox row for the same order → still 1/1; this is acceptance #1), `OrderPaid_email_links_to_the_order_and_has_no_qr`, `RefundRequested_and_OrderRefunded_each_notify_once_with_amount_in_vnd`, `PaymentRefunded_notifies_the_order_owner`, `OrderExpired_is_in_app_only`, `Handler_for_deleted_user_is_a_no_op`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** the five handlers (load order + event title once, build the draft, `writer.AddAsync`, push `OrderStatusChanged` with the order's status name when the writer returned an id).
- [ ] **Step 4: Run** → PASS; then the existing checkout/refund suites to prove no regression: `dotnet test tests/TicketRush.Api.IntegrationTests -- --max-threads 2 --filter-class "*Checkout*"`.
- [ ] **Step 5: Commit** `feat(09): notify buyers of paid, refunded and expired orders`

---

### Task 5: Event cancelled fan-out

**Files:**
- Create: `Infrastructure/Notifications/EventCancelledNotificationHandler.cs`
- Modify: `Infrastructure/DependencyInjection.cs`
- Test: `tests/TicketRush.Api.IntegrationTests/Notifications/EventCancelledNotificationTests.cs`

**Interfaces:** `EventCancelledNotificationHandler : IIntegrationEventHandler<EventCancelled>`. Recipients = distinct users of orders with status `Paid`, `RefundPending` or `Refunded` for the event, union `event_follows`. Two set-based `INSERT … SELECT … ON CONFLICT (dedupe_key) DO NOTHING` statements (notifications, then email deliveries joined to confirmed-email users), executed in the handler's transaction. After the insert, push only to users whose row was newly inserted (`RETURNING user_id`), in chunks of 500.

- [ ] **Step 1: Failing tests**: `Holders_and_followers_are_notified_once_each` (user both holder and follower → 1 row), `Redelivery_creates_no_duplicates`, `Unrelated_users_and_pending_orders_are_not_notified`, `Ten_thousand_recipients_use_a_constant_number_of_statements` (count via an EF command interceptor, ≤ 4 statements; seed with a bulk SQL insert).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS (also `EventCancelled` inventory tests still green).
- [ ] **Step 5: Commit** `feat(09): notify holders and followers when an event is cancelled`

---

### Task 6: User hub and live push

**Files:**
- Create: `Infrastructure/Realtime/UserHub.cs`, `Infrastructure/Realtime/UserNotifier.cs`
- Modify: `Api/ApiServiceCollectionExtensions.cs` (`app.MapHub<UserHub>(UserHub.Path)`), `Infrastructure/DependencyInjection.cs`, `web/next.config`/`api-rewrites` only if `/hubs/*` rewrite does not already cover it (it does: `/hubs/:path*`)
- Test: `tests/TicketRush.Api.IntegrationTests/Notifications/UserHubTests.cs`

**Interfaces:**
- Produces: `UserHub.Path = "/hubs/user"`, `UserHub.Group(Guid userId) => $"user:{userId:N}"`, methods `Notification` (arg: `NotificationItem`) and `OrderStatusChanged` (args: `orderId`, `status`); `UserHub` is `[Authorize]` and joins the group in `OnConnectedAsync` from the `sub` claim (no client-callable join). `record NotificationItem(Guid Id, string Type, string Title, string Body, string? Link, bool Read, DateTimeOffset CreatedAt)` lives in Application.

- [ ] **Step 1: Failing tests** (real SignalR client against the test server): `Connection_without_token_is_rejected`, `User_receives_own_notification_on_every_tab`, `User_never_receives_another_users_notification`, `OrderStatusChanged_reaches_the_owner`, `Push_from_the_worker_side_reaches_the_api_through_the_backplane` (reuse the pattern of the existing `EventsHub` backplane test).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** `UserHub`, `UserNotifier : IUserNotifier` (over `IHubContext<UserHub>`; swallow and log push failures, they must never fail a handler). **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(09): /hubs/user live notifications`

---

### Task 7: Notifications and follow API

**Files:**
- Create: `Application/Features/Notifications/{ListNotifications,MarkNotificationRead,MarkAllNotificationsRead}/*` (query/command + handler), `Application/Features/Follows/{FollowEvent,UnfollowEvent,GetFollowState}/*`, `Api/Features/Notifications/NotificationEndpoints.cs`, `Api/Features/Follows/FollowEndpoints.cs`
- Modify: `Api/ApiServiceCollectionExtensions.cs` (`v1.MapNotificationEndpoints(); v1.MapFollowEndpoints();`)
- Test: `tests/TicketRush.Api.IntegrationTests/Notifications/{NotificationApiTests,FollowApiTests}.cs`, `tests/TicketRush.Application.Tests/Notifications/*`

**Interfaces:**
- `GET /api/v1/me/notifications?cursor=&limit=` → `{ items: NotificationItem[], nextCursor: string|null, unreadCount: int }`, ordered `(created_at, id)` descending, opaque cursor.
- `POST /api/v1/me/notifications/{id}/read` → 204 (idempotent); other user's id → 404 `notification.not_found`. `POST /api/v1/me/notifications/read-all` → 204.
- `POST|DELETE /api/v1/events/{id}/follow` → 204, idempotent (second POST/DELETE is also 204); POST on a non-Published event → 409 `event.not_followable`; unknown event 404. `GET /api/v1/events/{id}/follow` → `{ following: bool }`.

- [ ] **Step 1: Failing tests**: `List_is_keyset_paginated_newest_first_with_unread_count`, `Limit_is_capped_at_100`, `Read_marks_only_that_notification`, `Read_of_someone_elses_notification_is_404`, `Read_all_clears_unread_count`, `Anonymous_is_401`, `Follow_twice_is_204_and_one_row`, `Unfollow_when_not_following_is_204`, `Follow_a_draft_event_is_409`, `Follow_state_reflects_follow_and_unfollow`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** handlers and thin endpoints (follow insert uses the unique key; a unique violation means already following).
- [ ] **Step 4: Run** → PASS; regenerate the OpenAPI snapshot if the repo has one (`web` typed client generation script).
- [ ] **Step 5: Commit** `feat(09): notifications and follow API`

---

### Task 8: ReminderScheduler

**Files:**
- Create: `Worker/Notifications/ReminderSchedulerService.cs`, `Infrastructure/Notifications/ReminderPlanner.cs`, `Infrastructure/Notifications/ReminderDueHandler.cs`
- Modify: `Infrastructure/DependencyInjection.cs`, `Worker/Program.cs`
- Test: `tests/TicketRush.Api.IntegrationTests/Notifications/{ReminderPlannerTests,ReminderDueTests}.cs`

**Interfaces:**
- `ReminderPlanner.PlanAsync(DateTimeOffset now, CancellationToken ct) : Task<int>` inserts missing `reminder_dispatches` for followers (`SaleOpens30m`: a phase of a Published event with `opens_at ∈ (now, now+30 min]`; `EventStarts24h`: Published event with `starts_at ∈ (now, now+24 h]`) with `ON CONFLICT DO NOTHING RETURNING`, and appends `ReminderDue` to the outbox **only for returned rows**, in the same transaction. `ReminderSchedulerService : LeaderElectedBackgroundService` (lock `reminders`, 1 min tick via `TimeProvider`) calls it.
- `ReminderDueHandler : IIntegrationEventHandler<ReminderDue>` writes the notification (types `SaleOpensSoon` / `EventStartsSoon`, link `/events/{slug}`, dedupe `reminder:{kind}:{eventId}:{userId}`, templates `reminder-sale-opens` / `reminder-event-starts` with Vietnam-time text).

- [ ] **Step 1: Failing tests**: `Planner_emits_once_per_user_event_kind_across_repeated_runs`, `Two_planners_in_parallel_emit_once` (acceptance #3), `Event_outside_the_window_is_ignored`, `Draft_cancelled_or_ended_events_are_ignored`, `Unfollowed_user_gets_nothing`, `Follow_created_inside_the_window_is_reminded_on_the_next_tick`, `ReminderDue_handler_writes_notification_and_email_once_on_redelivery`, `Scheduler_runs_only_on_the_leader` (reuse the `QueueWorkersTests` two-instance pattern).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(09): reminder scheduler for followed events`

---

### Task 9: Web — notification client, bell, list page, toast

**Files:**
- Create: `web/src/lib/notifications/{api,types,realtime,keys}.ts`, `web/src/features/notifications/{notification-bell,notification-list,use-user-hub,notifications-page}.tsx`, `web/src/app/(buyer)/account/notifications/page.tsx`
- Modify: `web/src/components/shared/site-header.tsx` (render `<NotificationBell />` before `AccountLink`), `web/src/app/(buyer)/layout.tsx` (mount `useUserHub` via a small client component), `web/src/lib/i18n.ts` (copy), the typed API client regeneration
- Test: `web/src/lib/notifications/*.test.ts`, `web/src/features/notifications/*.test.tsx`, `web/e2e/notifications.spec.ts`

**Interfaces:**
- `connectUserFeed({ accessToken, onNotification, onOrderStatus, onResync }) : { stop(): Promise<void> }` — same reconnect/backoff contract as `connectSeatFeed` (reuse `retryDelayMs`; do not copy the code: extract the shared reconnect helper from `lib/seats/realtime.ts` if the duplication exceeds ~15 lines). Hub path `/hubs/user`; no group join call.
- Hooks: `useNotifications()` (infinite keyset query, latest 10 for the popover), `useMarkRead()`, `useMarkAllRead()`; query keys under `["notifications"]`, cleared on user change like the existing cache reset.
- Bell is a client island gated by `hasSessionHint`, shows a badge (`99+` cap, `aria-label` "N thông báo chưa đọc"), `Popover` with the latest 10, empty and error states, "Xem tất cả" → `/account/notifications`.

- [ ] **Step 1: Failing Vitest**: `Badge_shows_unread_count_and_caps_at_99_plus`, `Popover_lists_latest_ten_with_empty_state`, `Clicking_an_item_marks_it_read_and_navigates`, `Mark_all_read_clears_the_badge`, `Hub_notification_shows_a_toast_and_bumps_the_badge`, `Hub_reconnect_triggers_a_refetch`, `Bell_hidden_without_session_hint`, `List_page_loads_more_with_cursor`.
- [ ] **Step 2: Run** `npm --prefix web test -- notifications` → FAIL.
- [ ] **Step 3: Implement** the client, bell, page and hook; the header stays a server component (only the island is client). Check the 170 KB public-page JS budget test still passes (the bell must not pull SignalR into public bundles: load `use-user-hub` only in the buyer layout, the bell uses plain query).
- [ ] **Step 4: Playwright** `notifications.spec.ts`: buyer pays a mock order → bell shows 1 → popover opens → item opens the order → badge clears; axe clean in light and dark.
- [ ] **Step 5: Run** Vitest, Playwright chromium, `npm --prefix web run lint` and `typecheck` → PASS. **Step 6: Commit** `feat(09): notification bell, list page and live toasts`

---

### Task 10: Web — "Nhắc tôi" follow button and instant order status

**Files:**
- Create: `web/src/features/follow/{follow-button,use-follow}.tsx`
- Modify: `web/src/app/(public)/events/[slug]/` event header/actions component, `web/src/features/checkout/` result page polling hook (subscribe to `OrderStatusChanged`), `web/src/lib/notifications/realtime.ts` (expose an order-status subscription)
- Test: `web/src/features/follow/*.test.tsx`, `web/src/features/checkout/*.test.tsx`, `web/e2e/follow.spec.ts`

**Interfaces:** `useFollow(eventId) : { following: boolean|undefined, toggle(): void, pending: boolean }` — optimistic update of `["follow", eventId]`, rollback on error with an error toast; anonymous click → `/login?next=…`. The result page keeps its 1.5 s / 90 s polling and **additionally** refetches the order immediately when `OrderStatusChanged` arrives for its `orderId`.

- [ ] **Step 1: Failing Vitest**: `Follow_toggles_optimistically`, `Follow_rolls_back_and_toasts_on_failure`, `Anonymous_click_redirects_to_login`, `Button_label_switches_between_Nhac_toi_and_Da_bat_nhac_nho`, `Order_status_push_triggers_immediate_refetch`, `Polling_still_works_when_the_hub_never_connects`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Playwright** `follow.spec.ts`: follow, reload keeps state, unfollow. **Step 5: Run** all web suites → PASS.
- [ ] **Step 6: Commit** `feat(09): follow button and instant order status via hub`

---

### Task 11: Mailpit end-to-end render check, ops notes, spec bookkeeping

**Files:**
- Create: `tests/TicketRush.Api.IntegrationTests/Notifications/MailpitRenderTests.cs`, `docs/runbooks/email-delivery.md`
- Modify: `tests/TicketRush.Api.IntegrationTests/*.csproj` (Testcontainers generic is already available; add nothing unless needed), `specs/09-notifications-and-email.md` (tick acceptance with test names, add the `GET /events/{id}/follow` row, list the deferred 13/14 rows), `README.md` status row, `specs/06-virtual-waiting-room.md` untouched
- Test: the new Mailpit test

**Interfaces:** `axllent/mailpit` container started in the test fixture; the real `SmtpEmailSender` sends each template; the test reads `GET /api/v1/messages` and `/api/v1/message/{id}`.

- [ ] **Step 1: Failing test** `Every_template_arrives_in_mailpit_with_html_text_and_vietnamese_diacritics` (asserts both parts, `Content-Type: text/html; charset=utf-8`, subject decoded correctly, and that two sends with the same delivery id do not produce two messages when the processor is run twice). Skip with a clear reason only when Docker is unavailable.
- [ ] **Step 2: Run** → FAIL (until wired) then PASS. 
- [ ] **Step 3: Runbook**: how to read `email_deliveries` (Pending/Dead-lettered), requeue a dead-lettered row (SQL), what an SMTP outage looks like in logs and metrics.
- [ ] **Step 4: Full verification** (superpowers:verification-before-completion): Domain, Architecture, Application, Api (`-- --max-threads 2`), Vitest, Playwright chromium; compare with the baselines in Global Constraints and tick spec 09 acceptance with the test names.
- [ ] **Step 5: Commit** `docs(09): acceptance ticked, email delivery runbook`

---

## Self-review

- **Spec coverage:** notification table + all trigger rows → T1, T2, T4, T5, T8 (13/14 rows deliberately out, stated in the design); templates (Scriban, layout, diacritics, text part, logo only) → T2, T11; hub + `OrderStatusChanged` → T6, T10; follow + reminders → T7, T8, T10; API table → T7; frontend bell/popover/toast/"Nhắc tôi" → T9, T10; acceptance 1 → T4, 2 → T3, 3 → T8, 4 → T2, T11.
- **Type consistency:** `NotificationDraft`/`EmailPlan`/`NotificationItem`/`IUserNotifier` are defined in T3/T6 and consumed in T4/T5/T8 with the same names. `IUserNotifier` is declared in T3 and implemented in T6; T4 tests use a substitute until T6 lands, so T4 does not depend on T6 compiling beyond the interface.
- **Proportion:** the plan fixes names, signatures, keys, statements and test names; bodies are left to the implementer.
