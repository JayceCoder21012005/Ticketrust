"use client";

import { useCallback, useEffect, useEffectEvent, useState } from "react";
import { getQrKey, retryAfterSeconds, ticketFailure } from "@/lib/tickets/api";
import { keyCache, ticketCache } from "@/lib/tickets/key-cache";
import type { TicketFailure } from "@/lib/tickets/types";

/** `network`: the api's answer; `cache`: the key saved on an earlier online visit, used because the network failed. */
export type KeySource = "network" | "cache";

export type QrKeyState =
  /** Not asked for: the ticket cannot show a QR (listed, checked in, voided, event over). */
  | { status: "idle" }
  | { status: "loading" }
  | { status: "ready"; secret: string; source: KeySource }
  /** The api will not give this ticket a key: the ticket changed (409) or is not the caller's (404). */
  | { status: "refused"; code: "not_displayable" | "not_found" }
  /** 429: asked again automatically after `retryAfter` seconds (counting down). */
  | { status: "rateLimited"; retryAfter: number }
  /** 503, offline without a cached key, or anything else: the buyer can retry. */
  | { status: "error"; failure: TicketFailure };

/** What one attempt ended with. 429 keeps the device time to retry at, so the countdown survives re-renders. */
type Outcome =
  | { status: "ready"; secret: string; source: KeySource }
  | { status: "refused"; code: "not_displayable" | "not_found" }
  | { status: "rateLimited"; retryAt: number }
  | { status: "error"; failure: TicketFailure };

/**
 * What the cache needs to know: when the ticket's event ends, the server-corrected clock (C10), and who is asking (the
 * signed-in user's id, or null without a session) so the cache is only ever used by its owner.
 */
export type KeyContext = { endsAt: string; serverNow: () => number; userId: string | null };

/** The only key format the wallet can sign (spec 10, TR1 with 15-second steps). */
const FORMAT = "TR1";
const STEP_SECONDS = 15;

/** While a cached key is shown, the network is asked again after 5 s, then 10, 20, 40 and every 60 s. */
const REFRESH_FIRST_MS = 5_000;
const REFRESH_MAX_MS = 60_000;

const ignore = () => undefined;

/** One online `qr-key` request, classified. A fresh key is saved for offline use; a refusal deletes the saved one. */
async function fetchKey(ticketId: string, signal: AbortSignal, context: KeyContext): Promise<Outcome> {
  try {
    const key = await getQrKey(ticketId, signal);
    // A key the generator cannot use must never be drawn, nor kept.
    if (key.format !== FORMAT || key.stepSeconds !== STEP_SECONDS) return { status: "error", failure: "other" };
    // Claimed first (the gate in key-cache runs the claim's wipe before this write), so a new user's first key survives
    // the wipe of the previous user's cache. Without a known user nothing is stored.
    const { userId } = context;
    if (userId) {
      void keyCache
        .claim(userId)
        .then(() => keyCache.put(ticketId, { secret: key.secret, endsAt: context.endsAt }))
        .catch(ignore);
    }
    return { status: "ready", secret: key.secret, source: "network" };
  } catch (error) {
    const failure = ticketFailure(error);
    if (failure === "not_displayable" || failure === "not_found") {
      // Gone, listed, voided or checked in: what was saved for it is no longer valid.
      void keyCache.delete(ticketId).catch(ignore);
      void ticketCache.delete(ticketId).catch(ignore);
      return { status: "refused", code: failure };
    }
    if (failure === "rate_limited") return { status: "rateLimited", retryAt: Date.now() + (retryAfterSeconds(error) ?? 60) * 1000 };
    return { status: "error", failure };
  }
}

const running = (endsAt: string, nowMs: number) => {
  const end = Date.parse(endsAt);
  return Number.isFinite(end) && end > nowMs;
};

/**
 * One `qr-key` request, classified: the single place the wallet asks for a key. Only a NETWORK failure (no HTTP answer)
 * falls back to the key cached on an earlier visit, and only while its event is still running on the server clock; a
 * refusal (404/409) or an error answer (429/503) never does.
 */
export async function loadQrKey(ticketId: string, signal: AbortSignal, context: KeyContext): Promise<Outcome> {
  const outcome = await fetchKey(ticketId, signal, context);
  if (outcome.status !== "error" || outcome.failure !== "network" || signal.aborted) return outcome;

  const cached = await keyCache.read(ticketId, context.userId);
  return cached && running(cached.endsAt, context.serverNow()) ? { status: "ready", secret: cached.secret, source: "cache" } : outcome;
}

/**
 * The ticket's QR secret, asked for imperatively when `enabled` (the ticket is valid and the event running). It is a
 * credential: kept in this hook's state and the wallet's IndexedDB only, never in the TanStack cache, never logged. A
 * 429 counts down and asks again by itself. A cached key (offline) is shown at once while the network is retried in
 * the background with a bounded backoff, so the cache is refreshed as soon as the signal is back.
 */
export function useQrKey(
  ticketId: string,
  enabled: boolean,
  endsAt: string,
  offsetMs: number,
  userId: string | null,
): QrKeyState & { retry: () => void } {
  const [attempt, setAttempt] = useState(0);
  const [outcome, setOutcome] = useState<{ attempt: number; ticketId: string; value: Outcome } | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const retry = useCallback(() => setAttempt((n) => n + 1), []);
  const context = useEffectEvent((): KeyContext => ({ endsAt, serverNow: () => Date.now() + offsetMs, userId }));

  // A ticket that stops being displayable forgets its secret: if it becomes displayable again, nothing of the old
  // answer shows before a fresh one is in.
  if (!enabled && outcome !== null) setOutcome(null);

  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    loadQrKey(ticketId, controller.signal, context()).then((value) => {
      if (!controller.signal.aborted) {
        setNow(Date.now());
        setOutcome({ attempt, ticketId, value });
      }
    });
    return () => controller.abort();
  }, [ticketId, enabled, attempt]);

  // An outcome of an older attempt (or another ticket) is not the answer to this one: show loading meanwhile.
  const current = enabled && outcome && outcome.attempt === attempt && outcome.ticketId === ticketId ? outcome.value : null;
  const retryAt = current?.status === "rateLimited" ? current.retryAt : null;
  const fromCache = current?.status === "ready" && current.source === "cache";

  useEffect(() => {
    if (retryAt === null) return;
    const timer = setInterval(() => {
      const next = Date.now();
      setNow(next);
      if (next >= retryAt) setAttempt((n) => n + 1);
    }, 1000);
    return () => clearInterval(timer);
  }, [retryAt]);

  useEffect(() => {
    if (!fromCache) return;
    const controller = new AbortController();
    let delay = REFRESH_FIRST_MS;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const schedule = () => {
      timer = setTimeout(async () => {
        const value = await fetchKey(ticketId, controller.signal, context());
        if (controller.signal.aborted) return;
        // A fresh key or a refusal replaces the cached one. Anything else keeps the cached code on screen, deliberately
        // also on 429 and 503: the server did not refuse the ticket, it only could not answer now.
        if (value.status === "ready" || value.status === "refused") {
          setOutcome({ attempt, ticketId, value });
          return;
        }
        delay = Math.min(delay * 2, REFRESH_MAX_MS);
        schedule();
      }, delay);
    };

    schedule();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [fromCache, ticketId, attempt]);

  if (!enabled) return { status: "idle", retry };
  if (!current) return { status: "loading", retry };
  if (current.status === "rateLimited") return { status: "rateLimited", retryAfter: Math.max(0, Math.ceil((current.retryAt - now) / 1000)), retry };
  return { ...current, retry };
}
