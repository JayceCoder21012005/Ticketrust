# Spec 18 — MoMo payment (sandbox demo): design

**Branch:** `feat/18-momo-payment` (from `main` after spec 17 merged). There is no `specs/18-*.md`. This document is the spec.

## Intent

The deployed demo must take a payment through a real Vietnamese payment provider, so that a recruiter can click "Thanh toán", land on MoMo's own page, pay with a test card and come back to a ticket. This is a demo only: it runs against **MoMo's sandbox**, with no merchant account and no real money. Email comes after this, as a separate project.

## Decided with the owner

- **Demo only.**
  - Use MoMo's public sandbox credentials. They are published in MoMo's own sample code: `partnerCode=MOMO` (see References).
  - No merchant contract.
- **One approach: a MoMo adapter behind the existing gateway port.** It is approved as proposed.
- **The mock gateway stays** for development, CI, E2E and load tests.

## What exists today

- **`IPaymentGateway` (spec 08, Strategy).**
  - Its methods are `CreateSessionAsync`, `GetStatusAsync`, `RefundAsync` and `VerifyAndParse`.
  - `MockPaymentGateway` is the only implementation. It is wrapped by `ResilientPaymentGateway`, which adds the timeout, retries, circuit breaker and spec 17 metrics.
  - `PaymentGatewayResolver` picks gateways by `Provider`, and `Payment:Provider` picks the default (regex `^(Mock|VnPay)$`).
- **Webhooks.**
  - Webhooks arrive at `POST /api/v1/webhooks/payments/{provider}`. `PaymentWebhookReceiver` reads at most 64 KB as strict UTF-8, lets the gateway verify and parse, and hands the event to `FulfillOrderHandler`.
  - `FulfillOrderHandler` deduplicates by provider event id (`processed_webhooks`). It also never decides on one session's money twice, even under another event id.
  - The receiver answers 200 with a receipt.
- **`ExpireOrderHandler`.**
  - It already asks `GetStatusAsync` before expiring an order.
  - When a session reports `Succeeded`, it runs the webhook decision table with the synthetic event id `status:{sessionId}`.
- **Pay flow.**
  - `PayOrderHandler` passes `idempotencyKey = order:{orderId:N}:attempt:{n}`, with `returnUrl = {Frontend:BaseUrl}/checkout/{orderId}/result`.
  - `expiresAt` is the order's expiry minus `PaymentAttempt.GatewayMargin`.
  - The web redirects to `PaymentSession.RedirectUrl` (any absolute http(s) URL is allowed). The result page polls the order and listens on the user hub.
- **Caddy.** `deploy/Caddyfile` already routes `/api/v1/webhooks/*` straight to the api, so a MoMo IPN reaches it untouched.

## MoMo API (AIO v2), as used here

These details come from MoMo's official samples: `momo-wallet/payment` (`nodejs/MoMo.js`, `php/PayMoMo/ipn_momo.php`) and the developers.momo.vn pages for create, query and refund. Every signature is **HMAC-SHA256 with the secret key, as lowercase hex**, over `key=value` pairs. The pairs are joined by `&`, the keys are in alphabetical order, and the values are not URL-encoded.

**Hosts:**

| Environment | Host |
|---|---|
| Sandbox | `https://test-payment.momo.vn` |
| Production | `https://payment.momo.vn` |

**Minimum client timeout:** MoMo asks for 30 s.

### `POST /v2/gateway/api/create`

- **Request body:** `partnerCode, accessKey, requestId, amount, orderId, orderInfo, redirectUrl, ipnUrl, extraData, requestType, signature, lang`. Optionally `orderExpireTime` (minutes) and `autoCapture`.
- **Signature string:**
  ```
  accessKey=…&amount=…&extraData=…&ipnUrl=…&orderId=…&orderInfo=…&partnerCode=…&redirectUrl=…&requestId=…&requestType=…
  ```
- **Response:** `resultCode` (0 means created), `message` and `payUrl`, among others.
- **`requestType`:** `payWithMethod`. MoMo's page then offers the wallet QR **and** test ATM or credit cards, so a recruiter needs no MoMo app.

### IPN

- **What arrives:** a JSON POST to `ipnUrl` with `partnerCode, orderId, requestId, amount, orderInfo, orderType, transId, resultCode, message, payType, responseTime, extraData, signature`.
- **Signature string.** `accessKey` is not in the body; it is our configured one:
  ```
  accessKey=…&amount=…&extraData=…&message=…&orderId=…&orderInfo=…&orderType=…&partnerCode=…&payType=…&requestId=…&responseTime=…&resultCode=…&transId=…
  ```
- **Reply:** MoMo expects **HTTP 204**.

### `POST /v2/gateway/api/query`

- **Request body:** `partnerCode, accessKey, requestId, orderId, lang, signature`.
- **Signature string:**
  ```
  accessKey=…&orderId=…&partnerCode=…&requestId=…
  ```
- **Response:** `resultCode, transId, amount, refundTrans[]` (each with `orderId, amount, resultCode, transId`), among others.

### `POST /v2/gateway/api/refund`

- **Request body:** `partnerCode, accessKey, requestId, orderId, amount, transId, lang, description, signature`. The `orderId` must be new, distinct from the payment's.
- **Signature string:**
  ```
  accessKey=…&amount=…&description=…&orderId=…&partnerCode=…&requestId=…&transId=…
  ```

### `resultCode` handling

| Code | Treated as |
|---|---|
| `0` | Success |
| `9000` | Success (authorized, `autoCapture=true`) |
| `1000` | Pending, the buyer has not paid yet |
| `7000`, `7002` | Pending, processing |
| Anything else | Failed |

The other codes include `1005` (expired), `1006` (the buyer cancelled), `1001`, `1002`, `1003`, `1004`, `1007`, `1017`, `1026`, `1080`, `1081`, `2019`, `4001`, `4100` and `49`. `1005` and `1006` map to `Expired` and `Failed` respectively, for `GetStatusAsync`.

**Note on verification.** My web reader is refused by developers.momo.vn. The field lists above come from MoMo's own sample code and from search-result excerpts of the official pages. They are confirmed **against the live sandbox** by an opt-in test (Testing), and any difference found there wins over this document.

**Spike, 2026-11-22.** I called the sandbox with the public keys, from a throwaway script.

| Call | Result |
|---|---|
| `create` (`payWithMethod`, `orderExpireTime=5`) | `resultCode 0`, with a `payUrl` on `test-payment.momo.vn/v2/gateway/pay` |
| `query` on that order | `resultCode 1000` ("chờ người dùng xác nhận"), `transId 0`, `refundTrans []` |
| `create` again, same `orderId` | `resultCode 41` ("duplicated orderId") |
| `query` on an unknown `orderId` | `resultCode 1005` ("expired or did not exist") |

The create and query signature strings above are therefore proven. The IPN and refund strings come from MoMo's samples and docs, and their tests are built to match them.

## Design

### 1. `MoMoPaymentGateway : IPaymentGateway` (Infrastructure/Payments/MoMo)

- **Name and wiring.**
  - `Provider = "momo"`. It is registered next to the mock and wrapped by `ResilientPaymentGateway` in the same way, under its own `GatewayResilience("momo", …)` circuit.
  - A typed `HttpClient` calls MoMo. Its timeout is above MoMo's 30 s minimum, and the host-wide resilience handlers are removed, because the decorator already retries.
  - The per-attempt timeout for momo comes from `Payments:MoMo:Timeout` (default 30 s).
- **Signing.** `MoMoSignature` builds every raw string from an ordered list of `(name, value)` pairs, so no field order lives in string literals. It signs with HMAC-SHA256 and lowercase hex, and verifies in fixed time (`CryptographicOperations.FixedTimeEquals`).
- **`CreateSessionAsync`.**
  - **The MoMo `orderId`** is derived from the idempotency key as `TR` + the first 40 hex characters of SHA-256(key). The key contains `:`, which MoMo's orderId charset refuses. The same key therefore always gives the same MoMo order (C2), and this `orderId` is the `sessionId` we store.
  - **Repeated calls with the same key.** The first successful response (`payUrl` and expiry) is cached in Redis under the key, with a TTL running to the session expiry plus a margin, as `MockSessionStore` does. A repeat call with the same key returns the cached session without calling MoMo.
  - **A response lost to a timeout.** If MoMo then answers "duplicate orderId" and nothing is cached, the call fails with `PaymentRejectedException`, and the buyer's next "Thanh toán" gets a new attempt number, hence a new key. This is a known edge, acceptable for a demo.
  - **Fields sent:**
    - `amount` in VND, as an integer. Any other currency, or an amount outside MoMo's limits, is refused with `PaymentRejectedException`. The limits come from config: `MinAmount` 1,000 and `MaxAmount` 50,000,000.
    - `orderInfo = "TicketRush {order number or short id}"`.
    - `redirectUrl = returnUrl`.
    - `ipnUrl = Payments:MoMo:IpnUrl`.
    - `extraData` = base64 of `{"orderId":"<guid>"}`, for support only and never trusted.
    - `requestType = payWithMethod`, `autoCapture = true`, `lang = vi`.
    - `orderExpireTime` = whole minutes until `expiresAt`, rounded down and at least 1.
  - **`requestId`** is a fresh GUID for each HTTP call.
- **`GetStatusAsync(sessionId)`** calls `/query` and maps the result as follows.

  | `resultCode` | Status |
  |---|---|
  | 0 / 9000 | `Succeeded` |
  | 1000 / 7000 / 7002 | `Pending` |
  | 1005, or "order not found" | `Expired` |
  | Anything else | `Failed` |

  A transport failure throws, so the decorator retries and the expiry sweep skips the order (`SkippedOutage`), as it does today.
- **`RefundAsync(sessionId, amount, idempotencyKey)`.**
  1. Call `/query` to get `transId` and `refundTrans`. A payment that did not succeed is refused with `payment_not_succeeded`.
  2. The refund `orderId` is `RF` + 38 hex characters of SHA-256(idempotencyKey), plus `-{n}`.
  3. If `refundTrans` already holds a successful entry whose `orderId` starts with that prefix, return it. This replays a success and never repeats it.
  4. Otherwise `n` is the number of earlier entries with the prefix. A declined retry therefore gets a fresh MoMo `orderId`, which matches the `IPaymentGateway.RefundAsync` remarks.
  5. Call `/refund` and map `resultCode` 0 to `Succeeded`. Anything else is `Failed`, with reason `momo_{resultCode}`.
- **`VerifyAndParse(headers, body)`.**
  - Parse the JSON body, rebuild the IPN signature string with our accessKey, and verify it. A missing or wrong signature raises `InvalidWebhookSignatureException`.
  - **Replay window:** `responseTime` (epoch milliseconds) must fall within `WebhookTolerance.Window`. Outside it, the IPN is refused as a bad signature, as the mock's stale timestamp is. A late success is still picked up by the confirm path and the expiry sweep.
  - **The resulting event:**
    - `ProviderEventId = "{transId}:{resultCode}"`
    - `SessionId = orderId`
    - `Type`: `Succeeded` for 0 or 9000. `Failed` for any other final code. A pending code (1000, 7000, 7002) raises `InvalidWebhookPayloadException`, so no decision is taken on it.
    - `Amount = new Money(amount, "VND")`
    - `OccurredAt` = `responseTime`
  - `partnerCode` must equal ours, or the payload is invalid.
- **Configuration: `Payments:MoMo`.**
  - Fields: `Endpoint` (default sandbox), `PartnerCode`, `AccessKey`, `SecretKey`, `IpnUrl`, `Timeout`, `MinAmount`, `MaxAmount`.
  - Validated on start only when `Payment:Provider = MoMo`. All keys are then required, and both URLs must be absolute https URLs (http is allowed in Development).
  - `Payment:Provider` accepts `MoMo`.
  - The secrets come from the environment or `.env`, never from appsettings. `.env.example` gets `MOMO_*` placeholders, with a comment pointing to MoMo's public sandbox values. This is a deliberate exception to "no secrets in the repo": the values are MoMo's public test keys and are named only in a comment and in the README.

### 2. Webhook reply

`PaymentWebhookReceiver` answers **204 No Content**, with no body, to a provider that asks for it. It answers 200 with the receipt otherwise, so the mock is unchanged. The choice is a property of the gateway registration, not a provider-name check in the receiver: an Infrastructure-side `IWebhookAcknowledgement` or similar, decided in the plan. Error statuses stay as they are (400, 401, 404, 413, 500). MoMo retries on non-2xx.

### 3. Confirm on return: `POST /api/v1/orders/{id}/payment/confirm`

- **Who can call it:** the owner only. Anyone else gets 404, as with the other order endpoints. It falls under the user's ordinary rate-limit policy and needs no idempotency key, because it is idempotent by construction.
- **What it does.**
  - It loads the order's open payment attempts and asks `GetStatusAsync` for each.
  - On `Succeeded`, it runs `FulfillOrderHandler` with the same synthetic event `status:{sessionId}` that the expiry sweep uses. It returns the order view.
  - Any other status changes nothing. A gateway outage returns the order unchanged, with no 5xx; the page keeps polling.
- **Why it is needed.** MoMo's IPN cannot reach a laptop, and it may arrive after the redirect. The query result is authoritative, because it comes from our own signed server-to-server call. The query parameters on MoMo's redirect are **never** trusted.
- **Web.**
  - The result page calls it once on arrival, and once more at each of the first few polling beats, while the order is still `AwaitingPayment`.
  - It ignores any failure of the call itself; polling continues as before.
  - The client function goes in `orders-api.ts`, and the OpenAPI types are regenerated.

### 4. Web: sandbox notice

- **The notice.** When the configured provider is MoMo, the pay panel shows one line in vi and en: payment goes through MoMo's test environment, use a test card, and here is the link to MoMo's test-card page. The card numbers are not hard-coded here, since MoMo owns them.
- **How the web knows the provider.** A build argument, `NEXT_PUBLIC_PAYMENT_PROVIDER` (`mock`, the default, or `momo`), set in `docker-compose.prod.yml` from `.env`.
- **Allowed redirect.** Nothing else changes in the web: `isSafeRedirect` already allows `https://test-payment.momo.vn`.

### 5. Deployment and documentation

- **`docker-compose.prod.yml`:**
  - `Payment__Provider: ${PAYMENT_PROVIDER:-Mock}`
  - `Payments__MoMo__*` from `.env`
  - `IpnUrl` defaults to `https://${PUBLIC_HOST}/api/v1/webhooks/payments/momo`
  - The web build argument.
- **README.** A section "Thanh toán MoMo (sandbox)":
  - how to switch the provider,
  - which keys to use,
  - how to test locally with a tunnel (`cloudflared tunnel --url http://localhost:5080`) for the IPN, or without one, relying on confirm on return,
  - what is not supported (real merchant onboarding, the edge of a lost create response).

## Testing

- **Unit: signatures** (`MoMoSignatureTests`).
  - Raw-string construction for create, IPN, query and refund, checked against **MoMo's own published sample**, with the sample's keys and its expected string.
  - HMAC output as lowercase hex.
  - A fixed-time mismatch is rejected.
- **Unit: parsing** (`MoMoPaymentGateway.VerifyAndParse`).
  - A valid IPN maps to `Succeeded`.
  - A failure code maps to `Failed`.
  - A pending code raises `InvalidWebhookPayloadException`.
  - A wrong signature, a wrong partnerCode, a stale `responseTime` and a tampered amount are each rejected.
- **Integration: fake MoMo.** A fake MoMo HTTP server runs in the test host (a stub `HttpMessageHandler` or a minimal Kestrel app), playing MoMo's side.
  1. `pay` returns MoMo's `payUrl`, and a double "pay" with the same key creates one MoMo order.
  2. An IPN signed like MoMo's gives a Paid order and an answer of 204.
  3. The same IPN again is a duplicate, and no second ticket is issued.
  4. A bad signature returns 401.
  5. Confirm on return, with the fake reporting success, gives a Paid order. A later IPN for the same payment changes nothing.
  6. Confirm while the fake reports pending changes nothing.
  7. Expiry with the fake reporting success fulfils late.
  8. A refund on a cancelled event refunds through `/refund`. A retry after a declined refund uses a new refund orderId, and a retry after a success replays it.
  9. With the fake down, `pay` returns 503 through the circuit, and confirm is a no-op.
- **Opt-in live sandbox test** (`MoMoSandboxTests`). It is skipped unless `MOMO_SANDBOX_LIVE=1`, and it is not part of CI.
  - It creates a real sandbox payment with the public keys. It asserts `resultCode 0` and a `payUrl` on `test-payment.momo.vn`, then queries the payment and asserts a pending state.
  - This proves our signature strings against MoMo itself. I run it once before pushing and record the result in the PR.
- **Web.**
  - Vitest for the confirm call on the result page: it is called while `AwaitingPayment`, never after the order settles, and its failure is ignored.
  - Vitest for the sandbox notice.
- **Existing suites stay green**, with the mock as the default provider.

## Non-goals

- Real merchant keys or production go-live. The code supports them through config, but they are not tested.
- MoMo's other flows: tokenization, subscriptions, QR at the POS, and the mobile SDK.
- Emails. That is the next project.
- Verifying the signatures of MoMo *responses* to our own calls. These come over TLS from the MoMo host we called; the IPN, which anyone could POST to us, is the one that matters.

## Left to the builder

- Exact class names and the shape of the 204-acknowledgement hook.
- How the fake MoMo is hosted in the integration tests.
- The vi and en copy of the notice.

## References

- MoMo sample code: https://github.com/momo-wallet/payment (`nodejs/MoMo.js`, `php/PayMoMo/ipn_momo.php`). There is no license, so it is used only as a reference and nothing is copied.
- MoMo developer pages:
  - https://developers.momo.vn/v3/docs/payment/api/wallet/onetime/
  - https://developers.momo.vn/v3/docs/payment/api/payment-api/query/
  - https://developers.momo.vn/v3/docs/payment/api/payment-api/refund/
  - https://developers.momo.vn/v3/docs/payment/api/other/signature/
