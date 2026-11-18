# 12 — Anti-bot & Rate Limiting

**Goal:** make the fair path the only practical path. Bots and scalpers should be slowed, capped and detected without hurting real fans (C7).

**Depends on:** 06, 07

## Defence layers

| Layer | Rule | Where |
|---|---|---|
| Identity | Buying needs a confirmed email; one queue entry and one hold per account | 03, 06, 07 |
| CAPTCHA | Cloudflare Turnstile on register, login after 3 failures, queue join | `ICaptchaVerifier` (Strategy: `TurnstileVerifier`, `FakeCaptchaVerifier` for dev/tests) |
| Admission | Admission token bound to the user, revocable | 06 |
| Purchase cap | `MaxTicketsPerUser` checked in Redis at hold time **and** in the DB at fulfilment (sum of the user's valid tickets for the event) | 07, 08 |
| Rate limits | Table below | API |
| Honeypot | Hidden field on register and checkout forms; filled → silently reject | Frontend + validator |
| Velocity flags | Same phone/card fingerprint on > 3 accounts, > 20 queue joins/min from one /24 subnet → flag the account for admin review (16), don't auto-ban | `SuspiciousActivityDetector` |

## Rate limiting

Distributed sliding-window limiter in Redis (`RedisSlidingWindowLimiter`, one Lua script, ZSET per key) plugged into ASP.NET Core's `RateLimiter` middleware as named policies. The in-memory limiter is **not** enough because the API runs on several instances.

| Policy | Partition | Limit |
|---|---|---|
| `global` | IP | 300 req / min |
| `auth` | IP + email | 10 / 15 min |
| `queue-join` | user | 5 / min |
| `queue-status` | user | 60 / min (above the server-suggested poll rate, so honest clients never hit it) |
| `holds` | user | 20 / min |
| `checkout` | user | 10 / min |
| `qr-key` | user | 10 / min |
| `gate-scan` | staff user | 5 / s |

- Rejected → 429 ProblemDetails `rate_limited` + `Retry-After`.
- Spec 10's `ticket.qr_key_rate_limited` (429, from its Redis fixed-window throttle) becomes the standard `rate_limited` code when the `qr-key` policy replaces that throttle; the web client classifies 429 by status, so nothing changes there.
- If Redis is down, the limiter fails **open** for reads and the global policy, and the protected write path still fails closed (00).
- Trust `X-Forwarded-For` only from the configured reverse proxy (`ForwardedHeadersOptions.KnownProxies`).

## Frontend

- Turnstile widget (lazy-loaded, invisible mode when possible) on the queue join button and register form.
- Friendly 429 handling: a toast with a countdown from `Retry-After`. Never silently retry in a loop.

## Acceptance criteria

- [x] A script that skips Turnstile can't join the queue (`captcha.failed`).
- [x] Two concurrent hold requests from one user whose total exceeds the cap → only one is written (see notes: the second gets the one-hold-per-account answer, not `hold.limit_exceeded`).
- [x] Bypassing Redis limits (direct DB race in a test) is still stopped by the fulfilment check in 08.
- [x] Limits hold across 2 API instances (integration test with 2 hosts sharing Redis).
- [x] Honeypot submissions get a generic success response and create nothing (register).

## Implementation notes (what differs from the text above)

- **Limiter.** `RedisRateLimiter` is the global limiter of ASP.NET Core's rate-limiting middleware: every request counts under `global` (ip), and under the named policy its endpoint declares with `.RequireRateLimit(...)`. The windows are ZSETs in Redis (`sliding_window.lua`, time from Redis `TIME`), so the limiter keeps no per-ip state in memory. Partitions are hashed in the key (`tr:{env}:rl:{policy}:{hash}`). It sits after authentication (the per-user policies need the user) and before authorization (refused requests count too). Limits are in `RateLimits:*` (`Enabled`, `Permit`, `Window` per policy); integration tests turn it off except in their own hosts.
- **Policies applied:** `auth` on register, login, forgot-password and reset-password (partition: ip + lower-cased `email` read from the JSON body, rewound for the endpoint); `queue-join`; `queue-status` (`GET /queue/me`); `holds` (`POST /holds`); `checkout` (`POST /orders` and `/orders/{id}/pay`).
- **`qr-key` and `gate-scan` keep their own Redis throttles** (spec 10 `IQrKeyThrottle`, spec 11 `IScanGuard`) with the same limits. The scan guard also answers a repeated payload with its first answer, which a generic limiter cannot do. Both answer 429 with `Retry-After`; the gate one already uses `rate_limited`, the qr-key one still uses `ticket.qr_key_rate_limited` (the web classifies 429 by status).
- **Redis down:** the limiter fails open and logs a warning. The write paths that need Redis fail closed on their own.
- **Forwarded headers:** `ForwardedHeaders:KnownNetworks` (CIDR list) restricts which hops `X-Forwarded-For` is trusted from. Unset, every hop is trusted, which is safe only while the api is reachable through the proxy alone (the production compose file).
- **CAPTCHA:** `ICaptchaVerifier` with `TurnstileVerifier` (production, `Captcha:Provider=Turnstile`, `Captcha:TurnstileSecret`) and `FakeCaptchaVerifier` (dev and tests), which accepts only Cloudflare's test token `XXXX.DUMMY.TOKEN.XXXX`. A Turnstile outage answers 503 `system.busy` (fail closed). Queue join needs a token only for someone not in the line yet; a member reloading the page does not. Sign-in needs one after 3 failures for the email in 15 minutes, counted in Redis per email whether or not the account exists, so the answer never reveals an account: no token → 400 `captcha.required`, bad token → 400 `captcha.failed`.
- **Web:** `<Turnstile>` loads Cloudflare's script only when `NEXT_PUBLIC_TURNSTILE_SITE_KEY` is set (a build argument in the web Dockerfile and the production compose file). Without it the web sends the test token, which a production api refuses, so a missing key fails loudly. A 429 shows a toast counting down from `retryAfterSeconds`; nothing is retried automatically.
- **Purchase cap:** one hold per account is enforced inside `hold_create.lua` together with `bought + requested <= cap`, so concurrent holds of one buyer write exactly one hold; the others get `hold.already_exists`/a stale-read retry rather than `hold.limit_exceeded`. A replacement hold is checked against the cap. The fulfilment check of spec 08 refunds an order over the cap (`purchase_limit_exceeded`).
- **Honeypot:** `website` on the register form (off-screen, `aria-hidden`, out of the tab order). Filled → 202 like a real registration, and nothing is created. **Not done on checkout yet:** `POST /orders` returns the order it created, and a fake order would send the client to a page that does not exist.
- **Velocity flags:** `SuspiciousActivityDetector` writes `account_flags` rows (one open flag per user and reason, `resolved_at` for the admin review of spec 16): `subnet_join_burst` for joins beyond 20 per minute from one /24 (IPv4) or /48 (IPv6), counted in Redis per wall-clock minute; `shared_phone` on every account holding a phone number used by more than 3 accounts (both `0…` and `+84…` spellings). Nothing is blocked. **Card fingerprints are not checked:** the mock gateway has none.

