import { hasSessionHint } from "@/lib/auth/session-hint";
import { keyCache } from "./key-cache";

/** `public/sw.js`: caches the wallet page shell and its static assets so `/tickets/[id]` opens without signal. */
export const WORKER_URL = "/sw.js";
/** Every cache the worker creates starts with this (it versions the rest); logout deletes them all. */
export const WORKER_CACHE_PREFIX = "ticketrush-wallet-";

const STATIC_PREFIX = "/_next/static/";

/**
 * What the page that registers the worker already loaded, for the worker to cache right away: the first visit happens
 * before the worker controls the page, so without this the shell would only be cached on the second visit. The worker
 * checks the list again and caches only the wallet page and `/_next/static/*`.
 */
function loadedUrls(): string[] {
  const urls = new Set<string>([location.pathname]);
  const add = (raw: string | null | undefined) => {
    if (!raw) return;
    try {
      const url = new URL(raw, location.href);
      if (url.origin === location.origin && url.pathname.startsWith(STATIC_PREFIX)) urls.add(url.pathname + url.search);
    } catch {
      // Not a url.
    }
  };
  for (const entry of performance.getEntriesByType?.("resource") ?? []) add(entry.name);
  document.querySelectorAll<HTMLScriptElement>("script[src]").forEach((script) => add(script.src));
  document.querySelectorAll<HTMLLinkElement>("link[href]").forEach((link) => add(link.href));
  return [...urls];
}

/**
 * Registers the wallet's service worker (scope `/`). Called by the buyer's ticket screens only (the wallet and
 * `/account/tickets`), for a visitor with a session hint, outside `next dev` (whose chunks are not content-hashed).
 * Never throws: without a worker the wallet still works online.
 */
export function registerTicketWorker(): void {
  try {
    if (process.env.NODE_ENV === "development" || !("serviceWorker" in navigator) || !window.isSecureContext) return;
    if (!hasSessionHint(document.cookie)) return;

    navigator.serviceWorker
      .register(WORKER_URL, { scope: "/", updateViaCache: "none" })
      .then(() => navigator.serviceWorker.ready)
      .then((registration) => registration.active?.postMessage({ type: "warm", urls: loadedUrls() }))
      .catch(() => undefined);
  } catch {
    // Blocked by the browser or a policy: online only.
  }
}

/**
 * Logout and user change: wipes everything the offline wallet kept for this user. The cached keys and ticket details,
 * the worker (unregistered) and every cache it created. Never throws; each part is independent.
 */
export async function forgetOfflineTickets(): Promise<void> {
  const unregister = async () => {
    if (typeof navigator === "undefined" || !("serviceWorker" in navigator)) return;
    const registrations = await navigator.serviceWorker.getRegistrations();
    await Promise.all(
      registrations
        .filter((registration) => {
          const worker = registration.active ?? registration.waiting ?? registration.installing;
          return worker ? new URL(worker.scriptURL).pathname === WORKER_URL : false;
        })
        .map((registration) => {
          // The worker keeps controlling this page until it is left: tell it to stop caching and drop its caches now.
          registration.active?.postMessage({ type: "retire" });
          return registration.unregister();
        }),
    );
  };
  const dropCaches = async () => {
    if (typeof caches === "undefined") return;
    const names = await caches.keys();
    await Promise.all(names.filter((name) => name.startsWith(WORKER_CACHE_PREFIX)).map((name) => caches.delete(name)));
  };

  await Promise.allSettled([keyCache.clearAll(), unregister(), dropCaches()]);
}
