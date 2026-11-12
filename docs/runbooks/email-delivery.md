# Runbook: email delivery

Spec 09. Every notification email is a row in `email_deliveries`, written in the same transaction as its in-app notice. The worker's `EmailDeliveryService` (`EmailDeliveryProcessor`) renders and sends the rows through MailKit SMTP (Mailpit locally). Handlers never talk to SMTP.

Two emails do not use this table: `confirm-email` and `reset-password`. Their outbox handlers (`SendConfirmationEmailHandler`, `SendPasswordResetEmailHandler`) send straight away, because the token is generated at send time and must not sit in a table. A failure there is a failed outbox message: it retries with the outbox backoff and is dead-lettered after 10 attempts (`processed_at IS NOT NULL AND last_error IS NOT NULL` in `outbox_messages`; counters `ticketrush.outbox.failed` / `ticketrush.outbox.dead_lettered`, tag `type`).

## Reading `email_deliveries`

`status` is one of `Pending`, `Sent`, `DeadLettered`.

- `Pending`: waiting for its first send (`attempts = 0`) or for a retry (`attempts > 0`, `next_attempt_at` in the future).
- `Sent`: the SMTP server accepted it (`sent_at` is set). `last_error` of an earlier failure is kept for the record.
- `DeadLettered`: parked; nothing ever picks it up again until a person requeues it. `last_error` says why:
  - it failed 30 times (`EmailDelivery:MaxAttempts`), or
  - it failed once with an error that retrying cannot fix (see "Permanent errors" below), or
  - `last_error = 'expired'`: it was never sent, because its moment had passed (see "Expiring emails"). `attempts` is not changed by an expiry.

Two columns help to read the table: `created_at` is when the email was queued (rows from before the column existed got the time of the migration), and `expires_at` is the moment after which the email is no longer true, or NULL for an email that is worth sending however late.

### Retry schedule (about a day)

The email worker has its own policy (`EmailRetryPolicy`), not the outbox's: 30 s after the first failure, doubled per failure with jitter (each wait between half and the full ceiling), capped at 1 hour, dead-lettered at the 30th failed attempt. The 29 waits add up to between about 11.5 and 23 hours (typically about 17), so an SMTP outage of hours only delays email. All numbers are settings, validated on start: `EmailDelivery:BaseDelay` (00:00:30), `EmailDelivery:MaxDelay` (01:00:00), `EmailDelivery:MaxAttempts` (30).

### Permanent errors: dead-lettered at once

Retrying cannot help, so the first failure parks the row (`attempts = 1`, Error log `... dead-lettered at once, retrying cannot help: {ErrorType}`):

- it cannot render: unknown template (`ArgumentException`), a stored model that lacks a member the template needs (`ScriptRuntimeException`), a model that is not a flat JSON object of strings (`JsonException`), a broken template file (`InvalidOperationException`);
- the SMTP server refused this recipient or this message for good: `SmtpCommandException` with a 5xx status for the recipient or the message (for example `550 mailbox unavailable`, `554 rejected`);
- the stored address cannot be parsed (`ParseException`).

A 5xx refusal of our sender or of the session (`553 sender rejected`, `530 authentication required`) is not treated as permanent: it would fail every email alike, so it counts as a transport failure (next section). Every 4xx is temporary and retried.

### Outage breaker

Transport failures are connection errors (`SocketException`, `IOException`, `SslHandshakeException`), timeouts (`TimeoutException`), protocol errors (`SmtpProtocolException`, `ServiceNotConnectedException`), authentication failures and every 4xx. After 5 of them in a row (`EmailDelivery:OutageFailureThreshold`) the pass ends with the Warning `SMTP looks down ({Failures} transport failures in a row); pausing email delivery`, and the loop pauses 5 s, doubling up to 5 minutes (`EmailDelivery:OutageMaxBackoff`). The rows the pass did not reach are not charged an attempt. The count is kept across passes until a send succeeds, so while SMTP stays down each later pass tries one row (a probe) and pauses again; the first successful send closes the breaker and the backlog goes out at full speed. Other failures (anything not listed above) are retried with backoff without counting towards the breaker.

### Expiring emails

The reminder templates (`reminder-sale-opens`, `reminder-event-starts`) get `expires_at` = the moment they announce (the sale opening, the event start). A row claimed at or after that moment is not sent: it is parked as `DeadLettered` with `last_error = 'expired'`, without an SMTP attempt (Information log `... expired before it could be sent; parked unsent`). The other templates (orders, refunds, cancellation) have no expiry.

### Queries

```sql
-- Backlog by status and template
SELECT status, template, count(*), min(next_attempt_at) AS oldest_due
FROM email_deliveries
GROUP BY status, template
ORDER BY status, template;

-- What is waiting right now, and how late it is
SELECT id, template, attempts, next_attempt_at, now() - next_attempt_at AS late_by, left(last_error, 120) AS last_error
FROM email_deliveries
WHERE status = 'Pending' AND next_attempt_at <= now()
ORDER BY next_attempt_at
LIMIT 50;

-- Retrying (failed at least once, still pending)
SELECT id, template, attempts, next_attempt_at, left(last_error, 120) AS last_error
FROM email_deliveries
WHERE status = 'Pending' AND attempts > 0
ORDER BY attempts DESC, next_attempt_at;

-- Dead-lettered because they failed (expired ones left out)
SELECT id, user_id, template, attempts, dedupe_key, created_at, left(last_error, 200) AS last_error, next_attempt_at
FROM email_deliveries
WHERE status = 'DeadLettered' AND last_error IS DISTINCT FROM 'expired'
ORDER BY next_attempt_at DESC;

-- Expired unsent (reminders whose moment had passed): expected after a long outage, nothing to do
SELECT template, count(*), min(created_at) AS first_queued, max(expires_at) AS last_expiry
FROM email_deliveries
WHERE status = 'DeadLettered' AND last_error = 'expired'
GROUP BY template;

-- Age of the pending backlog
SELECT template, count(*), now() - min(created_at) AS oldest
FROM email_deliveries
WHERE status = 'Pending'
GROUP BY template;
```

`last_error` is `ExceptionType: message`. An SMTP message can quote the recipient address (`550 mailbox x@y.z unavailable`), so `last_error` is personal data: read it in SQL only. Never return it from an API or an admin view (there is none today; the admin screens of spec 15 must leave it out). `to_address` and `model` are personal data too.

## Requeue a dead-lettered row

First fix the cause (see below), otherwise the row is parked again (at once for a permanent error, after 30 more attempts otherwise). The `status = 'DeadLettered'` guard makes sure a row that was already sent is never touched; if the update affects 0 rows, re-check before doing anything else.

**Never requeue an `expired` row.** It is a reminder of a moment that has passed ("the sale opens at 20:00" sent at 21:00). The `last_error IS DISTINCT FROM 'expired'` guard below keeps those rows out; requeued anyway, the worker would park them again unsent, since `expires_at` has not changed.

```sql
-- one row
UPDATE email_deliveries
SET status = 'Pending', attempts = 0, next_attempt_at = now()
WHERE id = '<delivery id>' AND status = 'DeadLettered' AND last_error IS DISTINCT FROM 'expired';

-- every dead-lettered row of one template, after fixing that template or its data
UPDATE email_deliveries
SET status = 'Pending', attempts = 0, next_attempt_at = now()
WHERE template = '<template>' AND status = 'DeadLettered' AND last_error IS DISTINCT FROM 'expired';
```

`last_error` is left in place on purpose. The worker picks the row up within `EmailDelivery:PollInterval` (2 s).

A row that dead-lettered because it cannot render (an unknown template name, or a stored model that lacks a member the template needs; `last_error` starts with `ArgumentException`, `ScriptRuntimeException`, `JsonException` or `InvalidOperationException`, `attempts = 1`) will fail again at once. Deploy the template or fix the `model` jsonb first. A recipient refused with a `550`-style error is refused again unless the address was fixed (it is frozen in `to_address` at write time).

## What an SMTP outage looks like

- Logs (worker, `EmailDeliveryProcessor`): one Warning per failed attempt, `Email delivery {DeliveryId} ({Template}) failed attempt {Attempts}; retry at {NextAttemptAt}: {ErrorType}`, one Error when a row is parked, `... dead-lettered after {Attempts} attempts: {ErrorType}`, and, once 5 transport failures in a row were seen, the Warning `SMTP looks down ...; pausing email delivery` once per pass (every 5 s to 5 min while the outage lasts). The exception type (for example `SocketException`, `TimeoutException`, `SmtpCommandException`) is logged and nothing else: no address, no body, no exception message, because an SMTP message may quote the address.
- A dropped connection or a hanging server surfaces as `TimeoutException`: every send is bounded by `EmailDelivery:SendTimeout` (30 s). The batch loop logs `Email delivery batch failed; retrying shortly` (Error) when the database itself is unavailable, and waits 5 s.
- Data: `Pending` rows with `attempts > 0` grow slowly (a handful per pass thanks to the breaker; most of the backlog keeps `attempts = 0`) and `next_attempt_at` moves out (up to 1 hour apart). The `Pending` count by `template` is the quickest gauge. There is no dedicated email metric; the `ticketrush.outbox.*` counters only cover outbox messages (account emails, notification handlers).
- In-app notices are unaffected: they are written in the handler's transaction and never wait for SMTP. Other notification types are not blocked either, since each row fails and reschedules on its own (`EmailDeliveryTests.Smtp_failure_reschedules_with_backoff_and_does_not_touch_other_deliveries`). A row that fails does not stop the rows after it; each rescheduled row is simply tried again later.
- Recovery needs no action while rows are still `Pending`: when SMTP is back the next probe succeeds (within 5 minutes at most), the breaker closes and the rows go out in `next_attempt_at` order. A row is parked only after about 11.5 to 23 hours of its own failures, so only an outage of most of a day leaves failed `DeadLettered` rows (requeue them as above). Reminders whose moment passed during the outage are parked as `expired`: that is intended, do not requeue them.

## Timeout, transactions and the remaining duplicate window

One row is one transaction: claim it with `FOR UPDATE SKIP LOCKED` (several worker instances never take the same row), render, send, record the outcome, commit. A row is marked `Sent` only after the SMTP server accepted the message and is committed on its own, so a later row that fails or hangs never rolls back an earlier one. A row cancelled in flight (worker shutdown) stays `Pending` and is not counted as a failure.

The send timeout is 30 s (`EmailDelivery:SendTimeout`, 50 ms to 10 min). While a send runs, that one row's lock is held, so a hung server delays only that row's batch position, not the other instances.

**Remaining duplicate window.** If the process crashes (or the database commit fails) after the SMTP server accepted the message but before that one row commits, the row is still `Pending` and the email is sent again after the restart. At most one email per crash is repeated. The `Message-ID` is `{delivery id as 32 hex digits}@ticketrush`, so a receiver that deduplicates on it drops the second copy; others show both. This is the accepted trade-off for not losing a notification. Replaying the same business event can never duplicate a row, because of the unique `dedupe_key` (`ux_email_deliveries_dedupe_key`).

## How reminders are planned

- `ReminderSchedulerService` (worker) ticks every 1 minute. Instances compete for the Redis lock `reminders`; the leader runs `ReminderPlanner.PlanAsync`.
- The planner is safe even if two instances plan at once (a lost lock, a failover): it inserts straight into `reminder_dispatches` with `ON CONFLICT DO NOTHING RETURNING`. The primary key `(user_id, event_id, kind)` is the unique key. Only the planner that actually inserted a row writes the `ReminderDue` outbox message for it, in the same transaction.
- Kinds: `SaleOpens30m` (a sale phase of a Published event opens in `(now, now + 30 min]`) and `EventStarts24h` (the event starts in `(now, now + 24 h]`), for followers (`event_follows`) only. A follow made inside the window is reminded on the next tick.
- The outbox rows have a NULL `aggregate_id` on purpose: the dispatcher claims a message only when no older pending message of the same aggregate exists, and a NULL never matches, so a fan-out to thousands of followers is claimable in one batch instead of one row per batch.
- `ReminderDueHandler` then writes the notice and email with the dedupe key `reminder:{kind}:{eventId}:{userId}`.
- Log: Information `Planned {Count} reminders` (event id 9101), only when the count is above 0.

```sql
-- What was planned for an event
SELECT user_id, kind, created_at FROM reminder_dispatches WHERE event_id = '<event id>' ORDER BY created_at;

-- Reminder messages still waiting in the outbox
SELECT id, attempts, next_attempt_at FROM outbox_messages
WHERE type LIKE '%ReminderDue%' AND aggregate_id IS NULL AND processed_at IS NULL;
```

To send a reminder again on purpose, delete its `reminder_dispatches` row and its `notifications`/`email_deliveries` rows with the same dedupe key; otherwise it is deliberately sent once.

## Add a new notification type

This is the seam specs 13 (waitlist offer) and 14 (ticket transferred or resold) use.

1. Add a value to `NotificationType` (`src/TicketRush.Domain/Notifications/NotificationType.cs`). It is stored as its name.
2. If it has an email, add a template pair under `src/TicketRush.Infrastructure/Email/Templates/`: `<name>.txt` (first line `Subject: ...`, a blank line, then the plain-text body) and `<name>.html` (the body only; the shared `_layout.html` wraps it). Vietnamese copy, inline CSS, no remote image except the logo. Use only plain `{{ value }}` interpolation in the HTML (no `| string.upcase`, `| string.append` and so on: converted text would skip the HTML escaping). Both files are embedded resources; add the template name to the `Templates` list in `EmailRenderingTests` and `MailpitRenderTests`. The model is a flat object of camelCase string members.
3. Write a handler, an `IIntegrationEventHandler<TEvent>` registered with `services.AddIntegrationEventHandler<TEvent, THandler>()` in `DependencyInjection`. Build a `NotificationDraft(userId, type, title, body, link, dedupeKey, new EmailPlan(template, model))` (email `null` for an in-app-only notice) and call `INotificationWriter.AddAsync`. For order events, `OrderNoticeSender` does this for you. The writer queues the email only when the user has a confirmed address, and writes notice and email together or not at all.
4. Choose the dedupe key from the business entity, never from the message id, for example `order-paid:{orderId}` or `reminder:{kind}:{eventId}:{userId}`. It is unique across `notifications` and `email_deliveries`, so a replay or a re-emitted event writes nothing. `AddAsync` returns `null` when nothing was written; push to the user's open tabs (`IUserNotifier`) only for a non-null id.
5. Test the handler like `OrderNotificationTests` (replay once, expect one notice and one delivery), and add the template to the Mailpit render test.

No change is needed in the delivery worker, the schema or the API: `type` and `template` are plain strings in their rows.
