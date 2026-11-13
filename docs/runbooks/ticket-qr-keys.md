# Runbook: ticket QR keys

Spec 10. Every ticket has a 32-byte secret (`tickets.qr_secret`). The buyer's browser derives the rotating QR from it (`TR1.<id>.<step>.<sig>`, a new code every 15 s), and the gate (spec 11) checks the code with the same secret. This runbook covers how the secrets are stored, what the `qr-key` throttle looks like in Redis, how the offline cache on a buyer's device works and is cleared, how to rotate a secret, and how a gate check is built.

## How the secrets are stored

- `tickets.qr_secret` is `bytea` and holds the secret **encrypted** with ASP.NET Data Protection (purpose `TicketRush.Tickets.QrSecret.v1`, `ProtectedBytesConverter`). The database alone is not enough to read a secret.
- The key ring lives in Redis, in the LIST `tr:{env}:dataprotection:keys` (`{env}` is `Redis__Environment`: `prod` in `docker-compose.prod.yml`, `dev` by default). Backup and restore of the ring are in the README, section "Khoá Data Protection (bí mật QR của vé)".
- **Losing the key ring makes every ticket secret unreadable.** Reading a ticket's `QrSecret` throws a `CryptographicException` from `Unprotect`:
  - `GET /tickets/{id}/qr-key` answers 500 (`GlobalExceptionHandler` logs `Unhandled exception for GET /api/v1/tickets/.../qr-key`).
  - The wallet then shows its error state with a retry button. The offline fallback does not apply: it covers network failures only, not a 500.
  - A device that already cached the key (see below) keeps showing a code only while it is offline. Gate verification (spec 11) fails for every ticket.
  - `GET /me/tickets` and `GET /tickets/{id}` keep working: they never select the secret.
  - The ways to lose the ring: deleting the `redis-data` volume, `FLUSHALL`, Redis without AOF restarting, changing `Redis__Environment` (a different key prefix). Restore it from the backup **before** the api and worker start, and check `LLEN` first (see the README).
- The only place the secret leaves the server is the `qr-key` response body (base64, `Cache-Control: no-store`). It is never in the list or detail answers, logs, error bodies or the query cache of the web app.

## `qr-key` rate limiting

`GET /tickets/{id}/qr-key` is limited to **10 requests per minute per user** (`RedisQrKeyThrottle`, a fixed window, `IQrKeyThrottle`). This is a stop-gap: spec 12 replaces it with its named `qr-key` policy (user, 10 per minute).

- Key: `tr:{env}:qr-key:{userId:N}`, a string counter. `userId:N` is the user's Guid as 32 hex digits, no dashes (for example `tr:prod:qr-key:0192f0e0aaaa7bbb8ccc0123456789ab`).
- One Lua script (`qr_key_throttle.lua`) does `INCR` and, on the first request of a window (or if the key somehow has no TTL), `PEXPIRE` to 60 s. The window starts at the first request and never slides: refused requests do not extend it.
- The throttle runs **before** any database access. A 404 (unknown or foreign ticket) and a 409 (`ticket.not_displayable`) also use a slot, so the endpoint cannot be used to probe ids.
- Over the limit: 429, ProblemDetails with `code: ticket.qr_key_rate_limited`, the header `Retry-After: <seconds left in the window>` and `retryAfterSeconds` in the body. The wallet counts the seconds down and retries by itself.
- Redis unreachable: the request fails closed with 503 `system.busy` (nothing is read from the database).

Inspect and reset one user (compose; the same `redis-cli` pattern as the README's key ring backup):

```bash
DC="docker compose -f docker-compose.prod.yml"
KEY=tr:prod:qr-key:<user id as 32 hex digits, no dashes>
$DC exec -T redis sh -c "redis-cli -a \"\$REDIS_PASSWORD\" --no-auth-warning GET $KEY"    # requests so far in this window
$DC exec -T redis sh -c "redis-cli -a \"\$REDIS_PASSWORD\" --no-auth-warning PTTL $KEY"   # ms left; -2 = no window
$DC exec -T redis sh -c "redis-cli -a \"\$REDIS_PASSWORD\" --no-auth-warning DEL $KEY"    # reset: the next request opens a new window
```

A window is gone after at most 60 s anyway, so a reset is rarely worth it. A user who keeps hitting the limit is usually a client stuck in a retry loop or an automated script; the wallet itself asks for one key per ticket opened, plus a background retry while it runs on a cached key (5 s, 10, 20, 40, then every 60 s, so at most about one request a minute).

## The offline cache on a device

What a device keeps, all same-origin and per browser profile:

| Where | Name | Holds |
|---|---|---|
| IndexedDB | database `ticketrush-tickets`, store `keys` | `{secret, endsAt}` per ticket: the secret as the base64 the api sent, **not encrypted on the client** |
| IndexedDB | same database, store `tickets` | the ticket detail JSON as the api sends it (no secret), for rendering the wallet offline |
| IndexedDB | same database, store `meta` | the owner (user id) of the cache |
| `localStorage` | `tr.wallet.owner` | the user id of the device's last signed-in owner of the cache |
| `localStorage` | `tr.wallet.server-offset` | the last server-clock offset with a timestamp (ignored after 7 days), so the code stays on server time offline |
| Cache Storage | `ticketrush-wallet-shell-v1`, `ticketrush-wallet-static-v1` | the HTML of `/tickets/<uuid>` pages and `/_next/static/*` files |
| Service worker | `/sw.js`, scope `/` | registered only by `/account/tickets` and the wallet |

How it behaves:

- A key and the ticket detail are written after an online `qr-key` success, after the cache is claimed for the signed-in user (another user's leftovers are wiped first). Without a known user nothing is stored.
- Offline (a network failure, and nothing else), the wallet renders from the cache and keeps rotating the code from the cached secret, as long as the event has not ended on the server clock. It retries the network in the background.
- A 404 or 409 from the api (a transferred, listed, voided or checked-in ticket, an ended or cancelled event) deletes the cached key; the wallet also replaces a stale `Valid` detail with the fresh one. Entries of ended or cancelled events are purged whenever the wallet or the ticket list opens.
- Wiped completely (the three stores, the worker's caches, the worker registration) on: sign-out, a change of signed-in user (also on the login page), a refused session refresh (4xx other than 429).
- A key cached on a device is a credential for that ticket until the event ends: anyone with access to the browser profile can read it. This is why the cache is removed at sign-out.

Clear it on one device or browser:

- Normal route: the user signs out (works offline too). That wipes everything above.
- Manual: browser developer tools, Application, "Clear site data" for the site origin (IndexedDB, `localStorage`, Cache Storage and the service worker go together). Or, in the console of the site: `indexedDB.deleteDatabase("ticketrush-tickets")`, then `localStorage.removeItem("tr.wallet.owner")`, then `(await navigator.serviceWorker.getRegistrations()).forEach(r => r.unregister())`, then `(await caches.keys()).filter(k => k.startsWith("ticketrush-wallet-")).forEach(k => caches.delete(k))`.
- There is no server-side way to reach a buyer's device. Voiding a ticket (or an ownership change, spec 14) stops the gate accepting its codes; the cached secret itself stays on the device until the wallet next reaches the api (the 404/409 deletes it) or the event ends.

## Rotating a ticket's secret

**There is no rotation yet.** A ticket's secret is created once, when the ticket is issued (`Ticket.Issue`, `RandomNumberGenerator.GetBytes(32)`), and never changes. The spec says a new secret is created whenever ownership changes: spec 14 (resale and transfer) does it by voiding the old ticket and issuing a new `Ticket` row with a new random secret for the same seat, so the old owner's cached key stops verifying.

Until then, if one ticket's secret leaked (for example a buyer reports a compromised device), the options are operational: void the ticket and issue a replacement through the same domain methods the order flow uses, in one transaction. Do not edit `tickets.qr_secret` by hand: the column is encrypted, and a plain value fails to unprotect.

If the Data Protection key ring itself must change, do not change the purpose string `TicketRush.Tickets.QrSecret.v1`: it would make every stored secret unreadable. Key ring rotation (the roughly 90-day automatic key change) is safe, as old keys stay in the ring and keep decrypting.

## Adding a gate verification (spec 11)

Everything about the format lives in `IQrCodeService` (Application) and `QrCodeService` (Infrastructure). The gate does not parse or hash anything itself. Parse, load the secret by ticket id, then verify:

```csharp
if (!qr.TryParse(scannedText, out var parsed))
{
    return Reject();                              // not a TR1 code: wrong prefix, shape, length or characters
}

var secret = await db.Tickets.AsNoTracking()
    .Where(t => t.Id == parsed.TicketId)          // QrSecret is decrypted by the value converter
    .Select(t => t.QrSecret)
    .SingleOrDefaultAsync(ct);

if (secret is null || !qr.Verify(parsed, secret, time.GetUtcNow().ToUnixTimeSeconds()))
{
    return Reject();                              // unknown ticket and bad signature look the same
}
// Then the gate rules of spec 11: ticket Valid (not Listed, Voided, already CheckedIn), right event, and the atomic check-in.
```

Points to keep:

- `TryParse` never throws and rejects anything that is not canonical (padding, whitespace, a leading zero in the step, over 128 characters). `Verify` never throws, compares in constant time, and accepts `|step - current| <= 1` (the code of the previous, current or next 15 s slot: 15 s of clock drift). It checks the step window before the HMAC.
- Use the server clock (`TimeProvider`) for `unixSeconds`, never anything from the scan.
- Answer an unknown ticket id and a wrong signature in the same way, so the gate is not an oracle for ticket ids.
- A valid signature does not mean admission: the ticket status and the event still decide. Check-in time is spec 11's to add (`checkedInAt` is not in the ticket responses yet).
- For tests, `shared/fixtures/qr-vectors.json` (1,000 vectors) and `QrCodeServiceTests` show the exact expected codes. The file is regenerated only with `UPDATE_QR_VECTORS=1` on the C# test, never by hand.

## Known limits

- **Key ring dependence.** The secrets are only as durable as the Data Protection key ring in Redis (see above). Back the ring up with the database.
- **No rotation yet.** Nothing replaces a ticket's secret until spec 14. The 15 s code rotation protects against screenshots, not against a leaked secret.
- **Throttle is a stop-gap.** A Redis fixed window (10 per minute per user), replaced by spec 12's `qr-key` policy. Fail closed: a Redis outage takes `qr-key` (and so first-time wallet opens) down, while devices that already cached a key keep working offline.
- **Secret on the client.** The cached secret is base64 in IndexedDB, with no client-side encryption. An XSS could read it, but could also read it from memory while the wallet is open. A hardening idea is to import it once as a non-extractable HMAC `CryptoKey` and store that instead.
- **Offline trust.** With no session, the offline wallet trusts the device's last signed-in owner (`tr.wallet.owner` against the cache's owner record), because there is no server to ask. If a session ends without a sign-out and another user signs in but never opens a ticket screen before going offline, the first user's cached ticket could still open for them. Any session start of another user, sign-out and a refused refresh wipe the cache.
- **Only previously opened tickets work offline.** A ticket must have been opened online on that device; a sibling ticket of the same order works offline only if it was opened online too. A first install with no signal shows "Chưa xem được vé này khi không có mạng".
- **What the service worker caches.** Only GET navigations to `/tickets/<uuid>` (network first, the cached HTML after a failure or 3 s) and same-origin `/_next/static/*` (cache first, newest 300 kept). Never `/api/*`, `/hubs/*`, other pages, other origins or non-GET requests. The HTML holds no user data and no secret.
- **Clock.** The wallet corrects the device clock with the server time (`/time`, offset kept for 7 days). A device whose clock is wrong and has never reached the server may show codes the gate rejects (more than one step of drift).
- **Check-in time.** The "Đã check-in" overlay shows no time until spec 11 adds `checkedInAt`.
- **Encoder.** The QR is drawn by the in-repo encoder (versions 1 to 10, level M, byte mode). A payload longer than 213 UTF-8 bytes throws; a real `TR1` code is about 50 characters, so this cannot happen with the current format.
