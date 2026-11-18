import { hasSessionHint } from "@/lib/auth/session-hint";
import { ApiError, type ProblemDetails } from "@/lib/errors";
import { forgetOfflineTicketsLater } from "@/lib/tickets/forget-offline";
import type { components } from "./schema";

export type SessionUser = components["schemas"]["UserAccount"];
export type Session = components["schemas"]["LoginResponse"];

/**
 * The session (access token + user) lives in module memory only: never localStorage/sessionStorage, so an
 * XSS can't lift it from storage (spec 03). The refresh token is an HttpOnly cookie the browser sends to
 * `/api/v1/auth` by itself; a reload restores the session through one refresh call.
 */
let session: Session | null = null;
/** The last refresh got no answer at all (no signal): the session may well still exist, it just cannot be restored now. */
let unreachable = false;
let inFlightRefresh: Promise<string | null> | null = null;
const listeners = new Set<() => void>();

type Fetch = typeof fetch;

// The api requires this header on refresh/logout as a CSRF guard (spec 03).
const CSRF_HEADERS = { "X-Requested-With": "fetch" };

export function getSession(): Session | null {
  return session;
}

export function getAccessToken(): string | null {
  return session?.accessToken ?? null;
}

export function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function setSession(next: Session | null): void {
  session = next;
  if (next) unreachable = false;
  listeners.forEach((listener) => listener());
}

/**
 * True when the last session refresh got no usable answer: no network, or a busy one (5xx, 429, 511), not a refusal. The offline ticket
 * wallet reads this to stay on screen instead of sending the visitor to a login page that cannot load anyway.
 */
export function isSessionUnreachable(): boolean {
  return unreachable;
}

function setUnreachable(next: boolean): void {
  if (unreachable === next) return;
  unreachable = next;
  listeners.forEach((listener) => listener());
}

/** After a profile change, so everything showing the signed-in user stays in sync. */
export function updateSessionUser(user: SessionUser): void {
  if (session) {
    setSession({ ...session, user });
  }
}

export function clearSession(): void {
  setSession(null);
}

/** `captchaToken`: Turnstile's, sent once the api asked for one (`captcha.required`, after 3 failed sign-ins: spec 12). */
export async function login(
  credentials: { email: string; password: string; captchaToken?: string | null },
  fetchImpl: Fetch = fetch,
): Promise<Session> {
  const response = await fetchImpl("/api/v1/auth/login", {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(credentials),
  });

  if (!response.ok) {
    throw new ApiError(response.status, await problemOf(response));
  }

  const next = (await response.json()) as Session;
  setSession(next);
  return next;
}

/**
 * Rotates the refresh cookie and stores the new session. Every caller shares one in-flight request:
 * presenting the same refresh token twice is treated as theft and ends the session (spec 03).
 */
export function refreshAccessToken(fetchImpl: Fetch = fetch): Promise<string | null> {
  inFlightRefresh ??= acrossTabs(() => doRefresh(fetchImpl)).finally(() => {
    inFlightRefresh = null;
  });
  return inFlightRefresh;
}

const REFRESH_LOCK = "tr-refresh";

/**
 * The single flight above only covers one tab. Two tabs refreshing at once (two public pages restoring their session, say)
 * would present the same refresh cookie twice: the server rotates it for the first and treats the second as reuse, revoking
 * the whole token family and signing the user out everywhere (spec 03). A Web Lock shared by the origin's tabs serialises
 * the refreshes, so the second tab waits and then sends the cookie the first one already rotated. Without the API (older
 * browsers) this runs unguarded, as before. The underlying gap (no grace window for a just-rotated token) is spec 03's.
 */
function acrossTabs<T>(run: () => Promise<T>): Promise<T> {
  const locks = typeof navigator === "undefined" ? undefined : navigator.locks;
  return typeof locks?.request === "function" ? locks.request(REFRESH_LOCK, run) : run();
}

export async function logout(fetchImpl: Fetch = fetch): Promise<void> {
  try {
    await fetchImpl("/api/v1/auth/logout", { method: "POST", credentials: "same-origin", headers: CSRF_HEADERS });
  } catch {
    // Signing out locally must work offline too; the refresh token then simply expires.
  } finally {
    clearSession();
  }
}

export function loginRedirectUrl(currentPathAndQuery: string): string {
  return `/login?returnUrl=${encodeURIComponent(currentPathAndQuery)}`;
}

/** The api answered that nobody is signed in (not "busy", not "too many requests"). */
const refusesSession = (status: number) => status >= 400 && status < 500 && status !== 429;

async function doRefresh(fetchImpl: Fetch): Promise<string | null> {
  const hinted = typeof document !== "undefined" && hasSessionHint(document.cookie);
  try {
    const response = await fetchImpl("/api/v1/auth/refresh", { method: "POST", credentials: "same-origin", headers: CSRF_HEADERS });
    // A busy answer (5xx, 429, a captive portal's 511) says nothing about the session, like no network at all: the
    // visitor counts as unreachable so the offline wallet stays, and only a real refusal wipes what is kept offline.
    setUnreachable(!response.ok && !refusesSession(response.status));
    if (!response.ok) {
      clearSession();
      // The hint said signed in, the api says the session is over (expired, revoked, signed out elsewhere): what this
      // device kept for offline use goes too. A network failure (below) never wipes: that is the offline case.
      if (hinted && refusesSession(response.status)) void forgetOfflineTicketsLater({ onlyIfStored: true });
      return null;
    }

    const next = (await response.json()) as Session;
    setSession(next);
    return next.accessToken;
  } catch {
    setUnreachable(true);
    return null;
  }
}

async function problemOf(response: Response): Promise<ProblemDetails> {
  try {
    return (await response.json()) as ProblemDetails;
  } catch {
    return { status: response.status };
  }
}
