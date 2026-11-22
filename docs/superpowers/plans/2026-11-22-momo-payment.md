# MoMo Payment (sandbox) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the demo take a payment through MoMo's sandbox. A buyer is redirected to MoMo's page, pays with a test card, and gets a ticket. The ticket comes from MoMo's IPN, or from a server-side status query when the buyer returns.

**Architecture:**
- A new `MoMoPaymentGateway : IPaymentGateway` lives in `Infrastructure/Payments/MoMo`. It is wrapped by the existing `ResilientPaymentGateway` and selected with `Payment:Provider = MoMo`.
- The webhook receiver answers 204 to MoMo.
- A new owner-only endpoint, `POST /orders/{id}/payment/confirm`, asks the gateway for the status and runs the existing fulfil path. It uses the synthetic event id `status:{sessionId}`, which the expiry sweep already uses.
- The web result page calls that endpoint on arrival. The pay panel shows a sandbox notice.

**Tech Stack:** .NET 10 / ASP.NET Core minimal APIs, `HttpClient`, `System.Security.Cryptography.HMACSHA256`, StackExchange.Redis, xUnit v3 + Shouldly, Testcontainers. On the web side: Next.js, Vitest and openapi-typescript.

**Spec:** `docs/superpowers/specs/2026-11-22-momo-payment-design.md`

## Global Constraints

- **Mock stays the default.** `Payment:Provider` default stays `Mock`. Every existing test, E2E and load test runs unchanged on the mock.
- **Signatures:**
  - HMAC-SHA256 keyed with `SecretKey`, encoded as lowercase hex.
  - Raw string: `k=v` pairs joined by `&`, keys in alphabetical order, values not URL-encoded.
  - Verification uses `CryptographicOperations.FixedTimeEquals`.
- **Raw-string field lists, verbatim from the spec.** Create and query were proven against the sandbox on 2026-11-22.

  | Call | Fields in the raw string |
  |---|---|
  | create | accessKey, amount, extraData, ipnUrl, orderId, orderInfo, partnerCode, redirectUrl, requestId, requestType |
  | IPN | accessKey, amount, extraData, message, orderId, orderInfo, orderType, partnerCode, payType, requestId, responseTime, resultCode, transId |
  | query | accessKey, orderId, partnerCode, requestId |
  | refund | accessKey, amount, description, orderId, partnerCode, requestId, transId |

- **`resultCode` meanings:**

  | Code | Meaning |
  |---|---|
  | 0, 9000 | Success |
  | 1000, 7000, 7002 | Pending |
  | 1005 | Expired or unknown order |
  | 41 | Duplicate orderId |
  | Anything else | Failed |

- **Session id:** `"TR" + lowercase hex of SHA256(UTF8(idempotencyKey))[..40]`.
- **Refund orderId:** `"RF" + hex[..38] + "-" + n`.
- **Amounts:** VND integers only. `MinAmount` defaults to 1000 and `MaxAmount` to 50,000,000.
- **Request settings:**
  - `requestType = "payWithMethod"`, `autoCapture = true`, `lang = "vi"`.
  - `orderExpireTime` = whole minutes until `expiresAt`, at least 1.
  - The per-attempt HTTP timeout for MoMo defaults to 30 s.
- **IPN rules:**
  - The IPN is acknowledged with **204** and no body.
  - `ProviderEventId = "{transId}:{resultCode}"`.
  - A pending code raises `InvalidWebhookPayloadException`.
  - `responseTime` (epoch ms) must be within `WebhookTolerance.Window`.
- **Secrets** come only from the environment or `.env`, never from appsettings. The README and `.env.example` may *name* MoMo's public sandbox keys `MOMO` / `F8BBA842ECF85` / `K951B6PE1waDMi640xX08PD3vg6EkVlz`.
- **Repo rules:**
  - Never stage `web/package-lock.json`.
  - Integration tests run with `--max-threads 2`.
  - Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Run `scripts/ci-local.ps1` (with `ENABLE_DESIGN_PAGE=1`) before pushing.

## Review Focus

1. **Double "Thanh toán", or a resilience retry after MoMo already created the order.** Exactly one MoMo order is created. The second call gets the cached `payUrl`; it never fails with 41 when a cache entry exists. *Test: Task 2.*
2. **An IPN for a payment already settled by confirm-on-return** (or the reverse) issues no second ticket and causes no refund. *Test: Task 4.*
3. **A forged or tampered IPN** is refused with 401 and changes nothing. That covers a changed amount, another partnerCode, and an old `responseTime`. *Test: Task 3.*
4. **A refund retried after a decline** uses a new MoMo orderId. A refund retried after a success replays that success and never pays twice. *Test: Task 2.*
5. **MoMo down or slow.** `pay` maps to 503 through the circuit. Confirm is a quiet no-op, and the result page keeps polling. *Test: Tasks 2 and 4.*

---

### Task 1: MoMo options and signature

**Files:**
- Create:
  - `src/TicketRush.Infrastructure/Payments/MoMo/MoMoOptions.cs`
  - `src/TicketRush.Infrastructure/Payments/MoMo/MoMoSignature.cs`
- Modify: `src/TicketRush.Infrastructure/Options/InfrastructureOptions.cs`. Change the `PaymentOptions.Provider` regex to `^(Mock|MoMo|VnPay)$`.
- Test:
  - `tests/TicketRush.Api.IntegrationTests/Payments/MoMo/MoMoSignatureTests.cs`
  - `tests/TicketRush.Api.IntegrationTests/Payments/MoMo/MoMoOptionsTests.cs`

**Interfaces:**
- **`MoMoOptions`** (`SectionName = "Payments:MoMo"`):

  | Property | Default |
  |---|---|
  | `Endpoint` | `"https://test-payment.momo.vn"` |
  | `PartnerCode` | |
  | `AccessKey` | |
  | `SecretKey` | |
  | `IpnUrl` | |
  | `Timeout` | 30 s |
  | `MinAmount` | 1000 |
  | `MaxAmount` | 50_000_000 |
  | `SessionMargin` | 1 h |

- **`MoMoOptionsValidator(IHostEnvironment, IOptions<PaymentOptions>)`** applies only when the provider equals `MoMo` (ignoring case). Then:
  - PartnerCode, AccessKey and SecretKey are required.
  - `Endpoint` and `IpnUrl` must be absolute https URLs. Http is allowed in Development.
  - `MinAmount` must be ≥ 1 and below `MaxAmount`.
- **`MoMoSignature`:**
  - `static string Raw(params (string Key, string Value)[] pairs)` sorts by key (ordinal) and joins.
  - `static string Sign(string raw, string secretKey)`.
  - `static bool Matches(string raw, string secretKey, string? signature)` returns false for a null or non-hex signature, compares in fixed time, and accepts upper- or lowercase hex.

- [ ] **Step 1: Write the failing tests**
  - **`Sign_matches_the_value_MoMo_accepted_in_the_sandbox`.** Use the exact create pairs from the spike: keys from the Global Constraints, amount `10000`, extraData `""`, `ipnUrl=https://example.com/ipn`, `orderId=TR78ade6f952d84d4a8f951d9f8b4a7a`, orderInfo `TicketRush test`, partnerCode `MOMO`, `redirectUrl=https://example.com/return`, `requestId=3877a124-e7c2-4b99-8261-d3a5f06ff7bf`, requestType `payWithMethod`.
    - Assert that `Raw(...)` equals the literal string in alphabetical order.
    - Assert that `Sign` equals the value the test computes once with an independent `HMACSHA256` call in the test. Do not hard-code a hex value.
  - **`Raw_orders_keys_alphabetically_whatever_the_argument_order`.**
  - **`Matches_rejects_null_wrong_and_non_hex_and_accepts_uppercase_hex`.**
  - **`Validator_requires_keys_only_when_MoMo_is_the_provider`.** Mock with empty keys gives Success. MoMo with empty SecretKey gives Fail mentioning `SecretKey`. MoMo in Production with an http `IpnUrl` gives Fail. MoMo in Development with http passes.
- [ ] **Step 2: Run the tests and confirm they fail.**
  Command: `dotnet test --project tests/TicketRush.Api.IntegrationTests --filter-class "*MoMoSignatureTests" --filter-class "*MoMoOptionsTests"`
  Expected: build error, `MoMoSignature` is not defined.
- [ ] **Step 3: Implement** `MoMoOptions`, `MoMoOptionsValidator` and `MoMoSignature`, and change the regex.
- [ ] **Step 4: Run the same command.** Expected: PASS.
- [ ] **Step 5: Commit** with the message `feat(18): MoMo options and HMAC signature`.

### Task 2: MoMo client and gateway: create, status, refund

**Files:**
- Create:
  - `src/TicketRush.Infrastructure/Payments/MoMo/MoMoClient.cs`: a typed `HttpClient` with the request and response records.
  - `src/TicketRush.Infrastructure/Payments/MoMo/MoMoSessionCache.cs`: Redis.
  - `src/TicketRush.Infrastructure/Payments/MoMo/MoMoPaymentGateway.cs`
- Modify:
  - `src/TicketRush.Infrastructure/Redis/RedisKeys.cs`: add `PayMoMoSession(string idempotencyKey)`, which produces `{Prefix}pay:momo:idem:{Hash(key)}`, and document it in the key list.
  - `src/TicketRush.Infrastructure/Payments/PaymentsServiceCollectionExtensions.cs`
- Test:
  - `tests/TicketRush.Api.IntegrationTests/Payments/MoMo/FakeMoMo.cs`
  - `tests/TicketRush.Api.IntegrationTests/Payments/MoMo/MoMoPaymentGatewayTests.cs`

**Interfaces:**
- **`MoMoClient(HttpClient, IOptionsMonitor<MoMoOptions>)`:**
  - `Task<MoMoCreateResponse> CreateAsync(MoMoCreateRequest, ct)`
  - `Task<MoMoQueryResponse> QueryAsync(string orderId, ct)`
  - `Task<MoMoRefundResponse> RefundAsync(string orderId, long amount, string transId, string description, ct)`
  - The client signs every request, using a fresh `requestId = Guid.NewGuid().ToString()` each time. A non-2xx answer, or a body that cannot be read, throws `HttpRequestException`, which the decorator retries.
  - `MoMoQueryResponse` carries `ResultCode`, `TransId` (long), `Amount`, `Message` and `RefundTrans` (a list of `{OrderId, Amount, ResultCode, TransId}`).
- **`MoMoPaymentGateway(MoMoClient, MoMoSessionCache, IOptionsMonitor<MoMoOptions>, TimeProvider) : IPaymentGateway`.** `ProviderName = "momo"`, plus `static string SessionIdFor(string idempotencyKey)` and `static string RefundOrderIdPrefix(string idempotencyKey)`.
- **`MoMoSessionCache(IConnectionMultiplexer, RedisKeys)`:** `Task<PaymentSession?> GetAsync(string key, ct)` and `Task SetAsync(string key, PaymentSession, TimeSpan ttl, ct)`.
- **DI.**
  - Register `MoMoClient` with `AddHttpClient<MoMoClient>(c => c.BaseAddress = Endpoint; c.Timeout = Timeout + 5 s).RemoveAllResilienceHandlers()`, inside `#pragma EXTEXP0001`.
  - Register `MoMoSessionCache` and `MoMoPaymentGateway`, plus a second `IPaymentGateway` singleton: `ResilientPaymentGateway(momo, new GatewayResilience("momo", options with Timeout = MoMoOptions.Timeout, …), metrics)`.
  - Add the options and the validator with `ValidateOnStart`.
- **`FakeMoMo : HttpMessageHandler`** is the test double for MoMo:
  - It keeps orders in memory, `{orderId → (amount, resultCode, transId, refunds)}`.
  - It answers `/v2/gateway/api/create` (41 for a duplicate orderId), `/query` (1005 for an unknown order) and `/refund`.
  - It **verifies each request's signature** with `MoMoSignature`. A mismatch answers `{resultCode: 11}` with HTTP 200, as MoMo does.
  - Helpers: `Pay(orderId)` sets resultCode 0 and a transId. `DeclineNextRefund()`. `Down = true` throws `HttpRequestException`. `CreateCalls` is a counter.
  - Helper `SignedIpn(orderId, resultCode, responseTime)` returns `(string body)`.

- [ ] **Step 1: Write the failing tests** in `MoMoPaymentGatewayTests`. Wire a real Redis from `ContainersFixture` and use a `FakeTimeProvider`.
  - **`CreateSession_returns_MoMo_payUrl_and_session_id_derived_from_the_key`.**
    - `SessionId == SessionIdFor(key)` and has length 42.
    - `RedirectUrl` is the fake's payUrl.
    - The fake saw `requestType=payWithMethod`, `amount` equal to the order total, `orderExpireTime >= 1`, and `ipnUrl` equal to the option.
  - **`CreateSession_twice_with_the_same_key_creates_one_MoMo_order`.** `CreateCalls == 1` and both results are equal.
  - **`CreateSession_refuses_non_VND_and_out_of_range_amounts`.** Throws `PaymentRejectedException`, with 0 calls.
  - **`CreateSession_refuses_an_already_expired_window`.** Throws `PaymentRejectedException`.
  - **`CreateSession_duplicate_orderId_without_cache_is_rejected`.** Pre-create on the fake, then call: `PaymentRejectedException`.
  - **`GetStatus_maps_result_codes`.** 1000 maps to Pending, 0 to Succeeded, 9000 to Succeeded, 1006 to Failed, and an unknown order to Expired.
  - **`Refund_pays_back_through_refund_with_transId_and_replays_a_success`.**
    - First call: Succeeded, and the fake recorded one refund with `orderId` starting with `RefundOrderIdPrefix(key)` and `-0`.
    - Second call with the same key: Succeeded, and still one refund.
  - **`Refund_after_a_decline_uses_a_new_orderId`.**
    - `DeclineNextRefund`, then call: Failed, with reason `momo_<code>`.
    - Call again: Succeeded, with the refund orderId ending `-1`.
  - **`Refund_of_an_unpaid_payment_fails_with_payment_not_succeeded`.**
  - **`Gateway_down_throws_HttpRequestException`.** Thrown on create, status and refund.
- [ ] **Step 2: Run** `dotnet test --project tests/TicketRush.Api.IntegrationTests --filter-class "*MoMoPaymentGatewayTests"`. Expected: build failure.
- [ ] **Step 3: Implement** the client, cache and gateway as the spec's §1 describes, and register the DI. `VerifyAndParse` throws `NotImplementedException` until Task 3.
- [ ] **Step 4: Run** the same command. Expected: PASS. Then run `--filter-class "*Payment*"` and `--filter-class "*WorkerCompositionTests"`. Expected: all PASS, with the mock untouched.
- [ ] **Step 5: Commit** with the message `feat(18): MoMo gateway (create with cached session, status, idempotent refunds)`.

### Task 3: IPN verification and the 204 acknowledgement

**Files:**
- Modify:
  - `MoMoPaymentGateway.cs`, for `VerifyAndParse`.
  - `src/TicketRush.Api/Features/Webhooks/PaymentWebhookReceiver.cs`
  - `src/TicketRush.Application/Abstractions/Payments/IPaymentGateway.cs`
- Test:
  - `MoMoPaymentGatewayTests`, extended.
  - `tests/TicketRush.Api.IntegrationTests/Payments/MoMo/MoMoWebhookTests.cs`: an end-to-end test against Postgres, built like `PaymentWebhookTests`, with `Payment:Provider=MoMo` and the test keys in the settings.

**Interfaces:**
- **`IPaymentGateway`** gets a default interface member: `bool AcknowledgesWithNoContent => false;`. MoMo returns true. The receiver returns `TypedResults.NoContent()` when it is true, and `Ok(receipt)` otherwise. The metric and log stay unchanged.

- [ ] **Step 1: Write the failing tests.**
  - **Unit:**
    - `VerifyAndParse_accepts_a_MoMo_signed_IPN`. The type is Succeeded, the event id is `"{transId}:0"`, `SessionId` is the orderId, `Amount` is the VND amount, and `OccurredAt` is `responseTime`.
    - `..._maps_1006_to_Failed`.
    - `..._pending_1000_is_an_invalid_payload`.
    - `..._rejects_tampered_amount_wrong_partner_stale_responseTime_missing_signature`. These raise `InvalidWebhookSignatureException`, except a wrong partner, which raises `InvalidWebhookPayloadException`.
  - **End to end** (`MoMoWebhookTests`). Each order and attempt is placed in the database with provider `"momo"` and `sessionId = SessionIdFor(...)`.
    - `Signed_success_IPN_pays_the_order_and_answers_204`. 204 with an empty body; the order is Paid and has tickets.
    - `Same_IPN_twice_is_a_duplicate_and_issues_no_second_ticket`. 204 twice, and the ticket count is unchanged.
    - `Forged_IPN_is_401_and_changes_nothing`.
    - `Mock_webhooks_still_answer_200_with_a_receipt`. A regression guard.
- [ ] **Step 2: Run** `dotnet test --project tests/TicketRush.Api.IntegrationTests --filter-class "*MoMo*" --max-threads 2`. Expected: FAIL (`NotImplementedException`, 200 instead of 204).
- [ ] **Step 3: Implement** `VerifyAndParse` as the spec's §1 describes, add the interface member, and branch the receiver.
- [ ] **Step 4: Run** `--filter-class "*MoMo*"` and `--filter-class "*PaymentWebhookTests"`. Expected: PASS.
- [ ] **Step 5: Commit** with the message `feat(18): verify MoMo IPNs and acknowledge them with 204`.

### Task 4: Confirm on return

**Files:**
- Create: `src/TicketRush.Application/Features/Orders/ConfirmPaymentHandler.cs`
- Modify:
  - `src/TicketRush.Application/DependencyInjection.cs`, to register the handler.
  - `src/TicketRush.Api/Features/Orders/OrderEndpoints.cs`
  - `web/openapi/ticketrush-api.json`, regenerated the way earlier specs did it: run the api in Development and fetch `/openapi/v1.json`.
  - `web/src/lib/api/schema.d.ts`, via `npm run api:gen`.
- Test:
  - `tests/TicketRush.Api.IntegrationTests/Payments/MoMo/ConfirmPaymentTests.cs`, using the api with `Payment:Provider=MoMo` and `FakeMoMo` plugged in through `ConfigureTestServices` (`AddHttpClient<MoMoClient>().ConfigurePrimaryHttpMessageHandler(() => fake)`).

**Interfaces:**
- **`ConfirmPaymentHandler(IAppDbContext, IPaymentGatewayResolver, FulfillOrderHandler, GetOrderHandler, TimeProvider, ILogger)`:**
  - `Task<Result<OrderResponse>> HandleAsync(Guid userId, Guid orderId, CancellationToken ct)`. Use the same response type `GetOrderHandler` returns.
  - Another user's order, or a missing one, is `NotFound`, as `GetOrderHandler` does it.
  - Only when the order is `AwaitingPayment` or `PaymentFailed`: for each attempt that is `Pending`, newest first, call `GetStatusAsync`. On `Succeeded`, call `fulfil.HandleAsync(attempt.Provider, new WebhookEvent($"status:{sessionId}", sessionId, Succeeded, attempt.Amount, now), ct)` and stop.
  - Gateway exceptions (`HttpRequestException`, `BrokenCircuitException`, `TimeoutRejectedException`, `PaymentGatewayUnavailableException`, i.e. whatever `ExpireOrderHandler` treats as an outage) are logged and swallowed.
  - The handler returns the fresh order view.
  - The `status:` event id format comes from the existing helper in `ExpireOrderHandler` (`StatusEventId`). Make it `internal static` and shared rather than duplicated.
- **Endpoint:** `orders.MapPost("/{id:guid}/payment/confirm", ConfirmAsync)`, with `.RequireRateLimit(RateLimitPolicies.Checkout)`, `.WithName("ConfirmOrderPayment")`, `.Produces<OrderResponse>()` and `.ProducesProblem(404)`.

- [ ] **Step 1: Write the failing tests.** Each runs through `POST /orders` and `/pay` over HTTP, as `PayOrderApiTests` does.
  - **`Pay_with_MoMo_returns_MoMo_payUrl`.** The redirect host is the fake's, and one MoMo order exists.
  - **`Confirm_after_MoMo_reports_success_pays_the_order`.** `fake.Pay(sessionId)`, then confirm: 200 with status Paid and tickets issued.
  - **`Confirm_then_IPN_for_the_same_payment_changes_nothing`.** Confirm, then post a signed IPN: 204, with no extra ticket and no refund pending.
  - **`IPN_then_confirm_changes_nothing`.**
  - **`Confirm_while_pending_changes_nothing`.** The status stays AwaitingPayment.
  - **`Confirm_with_MoMo_down_returns_the_order_unchanged`.** 200, not 5xx.
  - **`Confirm_on_someone_elses_order_is_404`.**
  - **`Expiry_sweep_fulfils_a_MoMo_payment_the_IPN_never_reached`.** Expire the order's clock, `fake.Pay`, then run `ExpireOrderHandler` through the existing harness: `FulfilledLate`.
  - **`Pay_with_MoMo_down_is_503`.**
- [ ] **Step 2: Run** `dotnet test --project tests/TicketRush.Api.IntegrationTests --filter-class "*ConfirmPaymentTests" --max-threads 2`. Expected: FAIL (404 for the route).
- [ ] **Step 3: Implement** the handler and the endpoint, then regenerate the OpenAPI JSON and the TypeScript types.
- [ ] **Step 4: Run** the same command plus `--filter-class "*OrderExpiry*"` and `--filter-class "*PayOrderApiTests"`. Expected: PASS.
- [ ] **Step 5: Commit** with the message `feat(18): confirm a payment on return by asking the gateway`.

### Task 5: Web: confirm on the result page, and the sandbox notice

**Files:**
- Modify:
  - `web/src/lib/checkout/orders-api.ts`: add `confirmPayment(id: string): Promise<Order>`, which posts `/api/v1/orders/{id}/payment/confirm` and returns `normaliseOrder`.
  - `web/src/features/checkout/result-view.tsx`
  - `web/src/features/checkout/pay-panel.tsx`
  - `web/messages/vi.buyer.json` and `web/messages/en.buyer.json`
  - `web/Dockerfile`: add `ARG NEXT_PUBLIC_PAYMENT_PROVIDER=mock` and pass it to the env.
- Create: `web/src/lib/checkout/payment-provider.ts`, exporting `paymentProvider(): "mock" | "momo"` (read from `process.env.NEXT_PUBLIC_PAYMENT_PROVIDER`, with anything unknown treated as mock) and `MOMO_TEST_GUIDE_URL = "https://developers.momo.vn/v3/docs/payment/onboarding/test-instructions/"`.
- Test:
  - `web/src/features/checkout/result-view.test.tsx`, extended.
  - `web/src/features/checkout/checkout-view.test.tsx`, extended, or a new `pay-panel.test.tsx`.

**Behaviour:**
- **Confirm calls.** `PollingResult` calls `confirmPayment(orderId)`:
  - once on mount,
  - again at the polling beats while `order.status` is `AwaitingPayment`, at most 3 calls in all,
  - never after the order settles.
- **Results of a confirm call.** A rejection is ignored. A successful result is fed to `refetch` (or the order is set directly), so the page updates without waiting for a beat.
- **The notice.** When `paymentProvider() === "momo"`, the pay panel shows a `role="note"` paragraph: the `checkout.pay.momoSandbox` text, and a link labelled `checkout.pay.momoSandboxLink` to `MOMO_TEST_GUIDE_URL`, with `target="_blank"` and `rel="noopener noreferrer"`.
- **Copy:**
  - vi: "Thanh toán qua MoMo ở môi trường thử nghiệm: không trừ tiền thật. Hãy chọn thẻ ATM/thẻ quốc tế và dùng thẻ test của MoMo."
  - vi link: "Xem thẻ test"
  - en: "Payment goes through MoMo's test environment: no real money moves. Choose ATM or international card and use one of MoMo's test cards."
  - en link: "See test cards"

- [ ] **Step 1: Write the failing Vitest tests.**
  - `calls confirm while the order awaits payment and stops once it is paid`
  - `ignores a failing confirm call and keeps polling`
  - `shows the MoMo sandbox note only when the provider is momo`. Stub the env with `vi.stubEnv`.
- [ ] **Step 2: Run** `npx vitest run src/features/checkout`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `npx vitest run src/features/checkout`, `npm run lint` and `npx tsc --noEmit`. Expected: PASS.
- [ ] **Step 5: Commit** with the message `feat(18,web): confirm the payment on return and a MoMo sandbox note`.

### Task 6: Deploy configuration, README, and the live sandbox check

**Files:**
- Modify:
  - **`docker-compose.prod.yml`.** In `x-dotnet-env`, set:
    - `Payment__Provider: ${PAYMENT_PROVIDER:-Mock}`
    - `Payments__MoMo__Endpoint: ${MOMO_ENDPOINT:-https://test-payment.momo.vn}`
    - `Payments__MoMo__PartnerCode`, `AccessKey` and `SecretKey` from `${MOMO_PARTNER_CODE:-}` and the matching variables.
    - `Payments__MoMo__IpnUrl: ${MOMO_IPN_URL:-https://${PUBLIC_HOST}/api/v1/webhooks/payments/momo}`

    Add the web build argument `NEXT_PUBLIC_PAYMENT_PROVIDER: ${WEB_PAYMENT_PROVIDER:-mock}`.
  - **`.env.example`.** Add a commented block for `PAYMENT_PROVIDER`, `MOMO_*` and `WEB_PAYMENT_PROVIDER`, naming MoMo's public sandbox values in a comment.
  - **`src/TicketRush.AppHost/AppHost.cs`.** Pass `Payment__Provider` and `Payments__MoMo__*` through from parameters or environment when set. The default stays Mock.
  - **`README.md`.** Add the section "Thanh toán MoMo (sandbox)", covering: switching the provider, the keys, a cloudflared tunnel for the IPN, testing locally with no tunnel (confirm on return), the test-card link, and the limits.
- Test:
  - **`tests/TicketRush.Api.IntegrationTests/Infrastructure/DeployConfigTests.cs`.** Add a test that `docker-compose.prod.yml` passes `Payments__MoMo__SecretKey` from `.env` and that `.env.example` holds no `MOMO_SECRET_KEY=` with a value.
  - **`tests/TicketRush.Api.IntegrationTests/Payments/MoMo/MoMoSandboxTests.cs`.** Use `[Fact(Explicit = true)]`, or skip unless `MOMO_SANDBOX_LIVE=1`. It runs a real create against `test-payment.momo.vn` with the public keys and asserts resultCode 0 and a payUrl host of `test-payment.momo.vn`. Then `GetStatusAsync` must be Pending.

- [ ] **Step 1: Write the deploy-config test.** Run it and confirm it fails.
- [ ] **Step 2: Edit the compose file, `.env.example`, AppHost and README.** Run the test and confirm it passes.
- [ ] **Step 3: Run the live sandbox test once.**
  Command: `MOMO_SANDBOX_LIVE=1 dotnet test --project tests/TicketRush.Api.IntegrationTests --filter-class "*MoMoSandboxTests"`
  Expected: PASS. Record the output for the PR.
- [ ] **Step 4: Run the full local CI.** Command: `scripts/ci-local.ps1`, with `ENABLE_DESIGN_PAGE=1`. Expected: every step green.
- [ ] **Step 5: Commit** with the message `feat(18): MoMo in the production compose, README and a live sandbox check`.
