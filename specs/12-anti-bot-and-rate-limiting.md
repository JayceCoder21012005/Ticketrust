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

- [ ] A script that skips Turnstile can't join the queue (`captcha.failed`).
- [ ] Two concurrent hold requests from one user whose total exceeds the cap → one fails `hold.limit_exceeded`.
- [ ] Bypassing Redis limits (direct DB race in a test) is still stopped by the fulfilment check in 08.
- [ ] Limits hold across 2 API instances (integration test with 2 hosts sharing Redis).
- [ ] Honeypot submissions get a generic success response and create nothing.
