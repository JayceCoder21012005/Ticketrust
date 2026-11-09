# Runbook: parked refunds

The refund worker (spec 08) sends back money for every order and payment session in `RefundPending`. When the gateway keeps declining a refund (8 declines by default, `Refunds:MaxDeclines`), the worker parks it. Missing data parks it immediately: `no_succeeded_session`, `several_succeeded_sessions`, `missing_refund_amount` or `unknown_provider`. A parked refund keeps its `RefundPending` status. It is never dropped, and it is never retried until a person resumes it.

There is no admin screen yet (tracked for spec 15). Until there is, use this SQL.

## Alert

- Gauge `ticketrush.payments.refunds_parked > 0`. Take the max over worker instances; only the leader reports it.
- Error log events 8213 ("NEEDS ATTENTION") and 8106 (amount mismatch after a status decision).

## Find them

```sql
SELECT id, refund_reason, refund_attempts, refund_last_error, updated_at
FROM orders
WHERE status = 'RefundPending' AND refund_needs_attention;

SELECT id, order_id, provider, refund_amount, refund_currency, refund_attempts, refund_last_error, updated_at
FROM payment_attempts
WHERE status = 'RefundPending' AND refund_needs_attention;
```

## Fix the cause, then resume

1. Read `refund_last_error`.
   - A gateway reason such as `payment_not_succeeded`, `invalid_amount` or `amount_exceeds_payment`: check the payment in the provider's dashboard.
   - `error: <ExceptionType>`: an adapter failure. Check the worker logs (event 8216).
2. Fix the cause: complete or correct the payment with the provider, deploy the adapter fix, or fill in the missing data.
3. Resume the refund. This resets the decline count and makes it due on the next pass (within 5 s). The `status = 'RefundPending'` guard makes sure you never touch a row that has already been refunded. If the update affects 0 rows, re-check before doing anything else.

```sql
-- order-level refund (idempotency key = the order id)
UPDATE orders
SET refund_needs_attention = false, refund_attempts = 0, refund_next_attempt_at = NULL
WHERE id = '<order id>' AND status = 'RefundPending' AND refund_needs_attention;

-- session-level refund (idempotency key = 'attempt:<attempt id>')
UPDATE payment_attempts
SET refund_needs_attention = false, refund_attempts = 0, refund_next_attempt_at = NULL
WHERE id = '<attempt id>' AND status = 'RefundPending' AND refund_needs_attention;
```

This is the same as calling `Order.ResumeRefund()` / `PaymentAttempt.ResumeRefund()`.

## If the money was refunded by hand at the provider

Do NOT resume. A resumed refund would ask the gateway again under the same idempotency key. If the manual refund used another key, the money would go out twice. Record the outcome with the team that owns payments. A status-change admin action is part of spec 15.
