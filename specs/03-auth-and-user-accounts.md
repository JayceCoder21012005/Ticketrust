# 03 — Auth & User Accounts

**Goal:** secure registration and login with short-lived access tokens and rotating refresh tokens, plus roles that every later spec relies on.

**Depends on:** 01, 02

## Domain

- `User` (ASP.NET Core Identity, `Guid` key): `Email`, `FullName`, `PhoneNumber`, `AvatarUrl`, `EmailConfirmed`, lockout fields.
- Roles: `Customer` (default), `Organizer`, `Admin`, `GateStaff`.
- `RefreshToken`: `Id`, `UserId`, `TokenHash` (SHA-256, never store the raw token), `FamilyId`, `ExpiresAt`, `RevokedAt`, `ReplacedById`, `CreatedByIp`, `UserAgent`.

## Token design

| Token | Format | Lifetime | Where it lives |
|---|---|---|---|
| Access token | JWT (HS256, key ≥ 32 bytes from config), claims `sub`, `email`, `role`, `jti` | 10 min | Frontend memory only (never localStorage) |
| Refresh token | 256-bit random, base64url | 14 days sliding, 30 days absolute | `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth` cookie |

The web app proxies `/api` through Next.js, so the cookie is first-party and `SameSite=Strict` blocks CSRF. Refresh and logout also require header `X-Requested-With: fetch`.

Because the refresh cookie is scoped to `/api/v1/auth`, page routes can't see it. Login also sets a non-secret hint cookie `tr_session=1` (`Path=/`, `SameSite=Lax`, same expiry), which is cleared on logout. `proxy.ts` uses only this hint for redirects.

**Rotation with reuse detection:** every refresh revokes the old token and issues a new one in the same family. If a revoked token is presented again, revoke the **whole family** (token theft) and log a security event.

## API

| Method | Route | Auth | Notes |
|---|---|---|---|
| POST | `/auth/register` | — | `{email, password, fullName}` → 202. Sends confirmation email |
| POST | `/auth/confirm-email` | — | `{userId, token}` |
| POST | `/auth/login` | — | `{email, password}` → `{accessToken, expiresAt, user}` + sets cookie |
| POST | `/auth/refresh` | cookie | → new access token + rotated cookie |
| POST | `/auth/logout` | cookie | Revokes the family, clears the cookie |
| POST | `/auth/forgot-password` | — | Always 202 (prevents email enumeration) |
| POST | `/auth/reset-password` | — | `{email, token, newPassword}`. Revokes all refresh tokens |
| GET/PUT | `/me` | user | Profile. Email change is out of scope |
| POST | `/me/avatar-upload-url` | user | Presigned PUT URL (image/*, ≤ 2 MB) |

Error codes: `auth.invalid_credentials` (same message for wrong email or wrong password), `auth.email_not_confirmed`, `auth.locked_out`, `auth.refresh_invalid`, `auth.weak_password`.

## Rules

- Password: ≥ 10 characters with letters and digits, not equal to the email. Identity's default hasher.
- Lockout: 5 failures → 15 min. Login is rate-limited per IP and per email (see 12).
- Unconfirmed email can log in but **cannot join a queue or buy** (`auth.email_not_confirmed`).
- Authorization policies: `RequireOrganizer`, `RequireAdmin`, `RequireGateStaff`. Never check role strings inside handlers.
- SignalR uses the access token through `accessTokenFactory` (query string `access_token`, only accepted on `/hubs/*`).

## Frontend

- Routes: `/login`, `/register`, `/verify-email`, `/forgot-password`, `/reset-password`, `/account`.
- `AuthProvider`: holds the access token in memory, calls `/auth/refresh` once on app load, refreshes 60 s before expiry, and uses one shared in-flight promise so parallel 401s trigger only one refresh.
- `proxy.ts` redirects users away from `(buyer)`, `/organizer`, `/admin`, `/gate` when the `tr_session` hint cookie is missing. This is UX only. API authorization stays the real guard.
- Forms: show/hide password, a strength meter, the submit button disables while pending, `?returnUrl=` after login (same-origin paths only, to prevent open redirects).
- Layout: centred card on mobile, split layout with event artwork on desktop.

## Acceptance criteria

- [ ] Reusing a rotated refresh token revokes the family; the next refresh with the newest token also fails.
- [ ] Wrong email and wrong password return the identical response body and similar timing.
- [ ] `forgot-password` returns 202 for unknown emails.
- [ ] Access token is never written to localStorage or sessionStorage (checked by a test).
- [ ] `returnUrl=https://evil.com` is ignored.
- [ ] Integration tests cover register → confirm → login → refresh → logout.
