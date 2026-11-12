"use client";

import { useCallback, useEffect, useState } from "react";
import { getQrKey, retryAfterSeconds, ticketFailure } from "@/lib/tickets/api";
import type { TicketFailure } from "@/lib/tickets/types";

export type QrKeyState =
  /** Not asked for: the ticket cannot show a QR (listed, checked in, voided, event over). */
  | { status: "idle" }
  | { status: "loading" }
  | { status: "ready"; secret: string }
  /** The api will not give this ticket a key: the ticket changed (409) or is not the caller's (404). */
  | { status: "refused"; code: "not_displayable" | "not_found" }
  /** 429: asked again automatically after `retryAfter` seconds (counting down). */
  | { status: "rateLimited"; retryAfter: number }
  /** 503, offline or anything else: the buyer can retry. */
  | { status: "error"; failure: TicketFailure };

/** What one attempt ended with. 429 keeps the device time to retry at, so the countdown survives re-renders. */
type Outcome =
  | { status: "ready"; secret: string }
  | { status: "refused"; code: "not_displayable" | "not_found" }
  | { status: "rateLimited"; retryAt: number }
  | { status: "error"; failure: TicketFailure };

/**
 * One `qr-key` request, classified. The single place the wallet asks for a key: task 6 (offline) adds the cached-key
 * fallback here, for a NETWORK failure only (a refusal must never be overridden by a cached key).
 */
export async function loadQrKey(ticketId: string, signal: AbortSignal): Promise<Outcome> {
  try {
    const key = await getQrKey(ticketId, signal);
    return { status: "ready", secret: key.secret };
  } catch (error) {
    const failure = ticketFailure(error);
    if (failure === "not_displayable" || failure === "not_found") return { status: "refused", code: failure };
    if (failure === "rate_limited") return { status: "rateLimited", retryAt: Date.now() + (retryAfterSeconds(error) ?? 60) * 1000 };
    return { status: "error", failure };
  }
}

/**
 * The ticket's QR secret, asked for imperatively when `enabled` (the ticket is valid and the event running). It is a
 * credential: kept in this hook's state only, never in the TanStack cache, never logged. A 429 counts down and asks
 * again by itself.
 */
export function useQrKey(ticketId: string, enabled: boolean): QrKeyState & { retry: () => void } {
  const [attempt, setAttempt] = useState(0);
  const [outcome, setOutcome] = useState<{ attempt: number; ticketId: string; value: Outcome } | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const retry = useCallback(() => setAttempt((n) => n + 1), []);

  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    loadQrKey(ticketId, controller.signal).then((value) => {
      if (!controller.signal.aborted) {
        setNow(Date.now());
        setOutcome({ attempt, ticketId, value });
      }
    });
    return () => controller.abort();
  }, [ticketId, enabled, attempt]);

  // An outcome of an older attempt (or another ticket) is not the answer to this one: show loading meanwhile.
  const current = outcome && outcome.attempt === attempt && outcome.ticketId === ticketId ? outcome.value : null;
  const retryAt = current?.status === "rateLimited" ? current.retryAt : null;

  useEffect(() => {
    if (retryAt === null) return;
    const timer = setInterval(() => {
      const next = Date.now();
      setNow(next);
      if (next >= retryAt) setAttempt((n) => n + 1);
    }, 1000);
    return () => clearInterval(timer);
  }, [retryAt]);

  if (!enabled) return { status: "idle", retry };
  if (!current) return { status: "loading", retry };
  if (current.status === "rateLimited") return { status: "rateLimited", retryAfter: Math.max(0, Math.ceil((current.retryAt - now) / 1000)), retry };
  return { ...current, retry };
}
