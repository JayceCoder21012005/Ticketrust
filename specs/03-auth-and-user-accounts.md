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

## Implementation notes

- `RefreshToken` also stores `CreatedAt` and `FamilyExpiresAt` (the 30-day absolute limit, copied on rotation). It is an aggregate, so it gets the `xmin` row version: when two refreshes present the same token at once, one wins and the other gets `auth.refresh_invalid` without revoking the family.
- Registering an email that already exists also returns 202, sends nothing and changes nothing (no account enumeration).
- Roles are reference data, inserted by the `AddAuth` migration (`ON CONFLICT DO NOTHING`), not only by the dev seed.
- Emails go through the outbox. `AppUser` raises `UserRegistered` / `PasswordResetRequested`; the worker generates the token when it sends (Data Protection keys are shared through Redis), so tokens never sit in `outbox_messages`. Confirmation links last 3 days, reset links 1 hour. Since spec 09 the copy comes from the Scriban templates `confirm-email` and `reset-password` (they still send directly from their outbox handlers, not through `email_deliveries`).
- The 5th failed login already answers `auth.locked_out`. Reset password also clears the lockout.
- Extra error codes: `auth.invalid_token` (confirm/reset links), `auth.csrf_header_missing` (403), `user.not_found`, `user.avatar_invalid`, `storage.upload_forbidden`, `storage.content_type_mismatch`, `storage.too_large`, `validation.failed`.
- The access token also carries `name` and `email_verified`. Queue and checkout (06, 08) enforce `auth.email_not_confirmed` from it.
- Policies are strict per role: `RequireOrganizer` admits Organizer only, not Admin.
- `tr_session` is not HttpOnly: the web reads it to skip the refresh call for anonymous visitors and to show the account link. Both cookies are `Secure`.
- Avatar storage: only the `Local` provider exists (`S3` fails at startup). The upload URL carries a time-limited Data Protection token for one key, type and size; `PUT /api/v1/storage/uploads` stores the file, which is served under `/media` (the web proxies `/media` like `/api`). Accepted types: JPEG, PNG, WebP, AVIF (no SVG). `PUT /me` only accepts avatar URLs under the user's own `avatars/<userId>/` prefix.
- Rate limiting of login and register is in spec 12.
- Worker and MigrationService images use `dotnet/aspnet`: Identity needs the ASP.NET Core shared framework.
- Form schemas use `zod/mini` and load on the first validation (`lazyResolver`), so zod isn't in first-load JS. The strength meter uses a zod-free copy of the password rules.
- In dark mode `--primary` is lighter (`oklch(0.72 0.16 285)`, dark text on it) so `text-primary` links pass 4.5:1 on cards. The token test now checks this too.
- `LoginForm` reads `?returnUrl=` from the address bar on submit, so `/login` stays a fully static page. Reading `searchParams` on the server made it partially prerendered, and the streamed part pushed the HTML past the first TCP window, adding ~150 ms to first paint. The header's login link has `prefetch={false}`, so public pages don't download the auth forms' JS.
- `proxy.ts` guards `/account`, `/my-tickets`, `/orders`, `/checkout`, `/queue`, `/organizer`, `/admin`, `/gate`.

## Acceptance criteria

- [x] Reusing a rotated refresh token revokes the family; the next refresh with the newest token also fails.
- [x] Wrong email and wrong password return the identical response body and similar timing.
- [x] `forgot-password` returns 202 for unknown emails.
- [x] Access token is never written to localStorage or sessionStorage (checked by a test).
- [x] `returnUrl=https://evil.com` is ignored.
- [x] Integration tests cover register → confirm → login → refresh → logout.
