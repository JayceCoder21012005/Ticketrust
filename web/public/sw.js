/*
 * TicketRush wallet service worker (spec 10): `/tickets/[id]` opens without signal at the venue.
 *
 * What it caches, and only this:
 * - the HTML of a wallet page (`/tickets/<id>`) that was opened online, network-first with a cache fallback (the page is
 *   rendered without the session, so it holds no user data; the ticket and its key come from the api into IndexedDB);
 * - same-origin `/_next/static/*` (scripts, styles, fonts), cache-first: they are content-hashed and never change.
 * Never: `/api/*`, `/hubs/*`, RSC/data requests, other pages, other origins, non-GET requests, non-200 answers.
 *
 * Registered by `src/lib/tickets/register-sw.ts`; logout unregisters it and deletes every `ticketrush-wallet-*` cache.
 * Bump VERSION when this file's caching changes: activation deletes the caches of older versions.
 */
const VERSION = "v1";
const PREFIX = "ticketrush-wallet-";
const SHELL_CACHE = `${PREFIX}shell-${VERSION}`;
const STATIC_CACHE = `${PREFIX}static-${VERSION}`;
const CURRENT = [SHELL_CACHE, STATIC_CACHE];
/** A slow network at the venue: after this long the cached page is shown (the network answer still updates the cache). */
const NETWORK_TIMEOUT_MS = 3000;
/** The static cache keeps the newest entries only (old deployments' chunks pile up otherwise). */
const MAX_STATIC_ENTRIES = 300;
const MAX_WARM_URLS = 300;

const WALLET_PAGE = /^\/tickets\/[^/]+\/?$/;
const STATIC_PREFIX = "/_next/static/";

/** Set by the page at logout (just before it unregisters this worker): from then on nothing is cached any more. */
let retired = false;

self.addEventListener("install", () => {
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    (async () => {
      const names = await caches.keys();
      await Promise.all(names.filter((name) => name.startsWith(PREFIX) && !CURRENT.includes(name)).map((name) => caches.delete(name)));
      await self.clients.claim();
    })(),
  );
});

const sameOrigin = (url) => url.origin === self.location.origin;
const isWalletPage = (url) => sameOrigin(url) && WALLET_PAGE.test(url.pathname);
const isStatic = (url) => sameOrigin(url) && url.pathname.startsWith(STATIC_PREFIX);
/** The wallet page is cached under its path alone (no query, no fragment). */
const pageKey = (url) => `${url.origin}${url.pathname.replace(/\/$/, "")}`;

/** A complete same-origin 200: never a redirect (to login), an error page, or an opaque answer. */
const cacheable = (response) => Boolean(response) && response.status === 200 && response.type === "basic" && !response.redirected;
const isHtml = (response) => (response.headers.get("content-type") || "").startsWith("text/html");

async function trim(cache) {
  const keys = await cache.keys();
  await Promise.all(keys.slice(0, Math.max(0, keys.length - MAX_STATIC_ENTRIES)).map((key) => cache.delete(key)));
}

async function storePage(url, response) {
  if (retired || !cacheable(response) || !isHtml(response)) return;
  const cache = await caches.open(SHELL_CACHE);
  await cache.put(pageKey(url), response);
}

async function storeStatic(request, response) {
  if (retired || !cacheable(response)) return;
  const cache = await caches.open(STATIC_CACHE);
  await cache.put(request, response);
  await trim(cache);
}

const timeout = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Network first; the cached page when the network fails or is too slow. */
async function walletPage(event, url) {
  const network = fetch(event.request).then((response) => {
    event.waitUntil(storePage(url, response.clone()).catch(() => undefined));
    return response;
  });
  event.waitUntil(network.catch(() => undefined));

  const cached = await caches.match(pageKey(url), { cacheName: SHELL_CACHE });
  if (!cached) return network;
  return Promise.race([network.catch(() => cached), timeout(NETWORK_TIMEOUT_MS).then(() => cached)]);
}

/** Cache first: a content-hashed file never changes. */
async function staticAsset(event) {
  // Content-hashed: whatever the request headers (a font asked for with CORS, say), the stored file is the one.
  const cached = await caches.match(event.request, { cacheName: STATIC_CACHE, ignoreVary: true });
  if (cached) return cached;
  const response = await fetch(event.request);
  event.waitUntil(storeStatic(event.request, response.clone()).catch(() => undefined));
  return response;
}

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (retired || request.method !== "GET") return;
  const url = new URL(request.url);
  if (!sameOrigin(url) || url.pathname.startsWith("/api/") || url.pathname.startsWith("/hubs/")) return;

  if (request.mode === "navigate" && isWalletPage(url)) {
    event.respondWith(walletPage(event, url));
  } else if (isStatic(url)) {
    event.respondWith(staticAsset(event));
  }
});

const parse = (raw) => {
  try {
    return new URL(raw, self.location.origin);
  } catch {
    return null;
  }
};

/**
 * What the page loaded before this worker controlled it (`register-sw.ts`): cached now, checked again here. The static
 * files first, all at once, then the page: once the page is cached, everything it needs is too.
 */
async function warm(raw) {
  const urls = raw.slice(0, MAX_WARM_URLS).map(parse).filter(Boolean);
  const staticCache = await caches.open(STATIC_CACHE);
  await Promise.all(
    urls.filter(isStatic).map(async (url) => {
      try {
        if (!(await staticCache.match(url.href, { ignoreVary: true }))) await storeStatic(new Request(url.href), await fetch(url.href));
      } catch {
        // Offline already, or the file is gone: the next online visit caches it.
      }
    }),
  );
  for (const url of urls.filter(isWalletPage)) {
    try {
      await storePage(url, await fetch(url.href, { credentials: "same-origin", headers: { Accept: "text/html" } }));
    } catch {
      // As above.
    }
  }
}

/** Logout: stop caching and delete every cache of this worker (the page unregisters it too). */
async function retire() {
  retired = true;
  const names = await caches.keys();
  await Promise.all(names.filter((name) => name.startsWith(PREFIX)).map((name) => caches.delete(name)));
}

self.addEventListener("message", (event) => {
  const data = event.data;
  if (event.origin && event.origin !== self.location.origin) return;
  if (data && data.type === "retire") {
    event.waitUntil(retire());
  } else if (data && data.type === "warm" && Array.isArray(data.urls)) {
    // A signed-in visitor registered this worker again (it can be the retired one, revived by the browser).
    retired = false;
    event.waitUntil(warm(data.urls.filter((url) => typeof url === "string")));
  }
});
