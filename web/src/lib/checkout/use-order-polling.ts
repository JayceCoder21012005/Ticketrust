"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "@/lib/errors";
import { isSettled } from "./order-state";
import { getOrder } from "./orders-api";
import type { Order } from "./types";

export const POLL_INTERVAL_MS = 1_500;
export const POLL_WINDOW_MS = 90_000;

export type PollPhase = "polling" | "settled" | "timeout" | "error";

type Options = { fetchOrder?: (id: string) => Promise<Order>; intervalMs?: number; windowMs?: number };

/** A missing or foreign order will not appear by waiting. */
const isGone = (error: unknown) => error instanceof ApiError && (error.status === 404 || error.status === 403);

/**
 * Follows an order until it settles (spec 08 result page): `GET /orders/{id}` every 1.5 s for at most 90 s. A hidden tab
 * neither fetches nor uses up the window (it resumes on return); a failed poll is tried again on the next beat.
 * `refresh` starts a new window, for the "still processing" state. `refetch` reads the order once now, whatever the
 * beat: for a push from the hub (spec 09) saying the order just changed. It never replaces polling, only goes ahead of it.
 */
export function useOrderPolling(orderId: string, { fetchOrder = getOrder, intervalMs = POLL_INTERVAL_MS, windowMs = POLL_WINDOW_MS }: Options = {}) {
  const [order, setOrder] = useState<Order | null>(null);
  const [phase, setPhase] = useState<PollPhase>("polling");
  const [round, setRound] = useState(0);
  const kick = useRef<(() => void) | null>(null);

  useEffect(() => {
    let active = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let beats = 0;
    // The first poll is at 0 s, the last at 90 s: that one still gets its answer before giving up.
    const maxBeats = Math.ceil(windowMs / intervalMs) + 1;

    const settle = (next: PollPhase) => {
      if (active) setPhase(next);
    };

    // One poll at a time: a tab that turns visible while a fetch is still out must not start a second chain.
    let inFlight = false;
    // A push that arrives while a fetch is out may be newer than that fetch: read again as soon as it lands.
    let readAgain = false;

    const poll = async (): Promise<"continue" | "stop"> => {
      try {
        const latest = await fetchOrder(orderId);
        if (!active) return "stop";
        setOrder(latest);
        if (isSettled(latest.status)) {
          settle("settled");
          return "stop";
        }
      } catch (error) {
        if (!active) return "stop";
        if (isGone(error)) {
          settle("error");
          return "stop";
        }
      }

      beats += 1;
      if (beats >= maxBeats) {
        settle("timeout");
        return "stop";
      }

      return "continue";
    };

    const beat = async (force = false) => {
      if (inFlight) {
        if (force) readAgain = true;
        return;
      }

      if (force || !document.hidden) {
        inFlight = true;
        const next = await poll().finally(() => {
          inFlight = false;
        });
        if (next === "stop") return;
        if (readAgain) {
          readAgain = false;
          void beat(true);
          return;
        }
      }

      timer = setTimeout(() => void beat(), intervalMs);
    };

    const onVisible = () => {
      if (!document.hidden && active) {
        clearTimeout(timer);
        void beat();
      }
    };

    kick.current = () => {
      if (!active) return;
      clearTimeout(timer);
      void beat(true);
    };

    void beat();
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      active = false;
      kick.current = null;
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [orderId, fetchOrder, intervalMs, windowMs, round]);

  const refresh = useCallback(() => {
    setPhase("polling");
    setRound((n) => n + 1);
  }, []);

  const refetch = useCallback(() => kick.current?.(), []);

  return { order, phase, refresh, refetch };
}
