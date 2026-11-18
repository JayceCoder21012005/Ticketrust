"use client";

import { useEffect, useState } from "react";
import { clockOffsetMs } from "@/lib/clock";
import { fetchServerTime } from "@/lib/server-time";

/** Where the last measured offset is kept across wallet opens and reloads. Not a secret: a number of milliseconds. */
const STORAGE_KEY = "tr.wallet.server-offset";
/** An offset older than this is not trusted any more (the device clock may have been changed since). */
const MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;
/** An offset this large is a damaged value, not a clock: it is treated as unknown. */
const MAX_OFFSET_MS = 24 * 60 * 60 * 1000;
/** Without a saved offset, the QR waits this long at most for `/time` before using the device clock. */
export const SERVER_TIME_WAIT_MS = 1_500;

type Saved = { offsetMs: number; at: number };

/** The last offset of this page (survives moving between the order's tickets, which remounts the wallet). */
let memory: Saved | null = null;

function fresh(saved: Saved | null): Saved | null {
  if (!saved || !Number.isFinite(saved.offsetMs) || !Number.isFinite(saved.at) || Math.abs(saved.offsetMs) >= MAX_OFFSET_MS) return null;
  const age = Date.now() - saved.at;
  return age >= 0 && age <= MAX_AGE_MS ? saved : null;
}

function readSaved(): Saved | null {
  if (memory) return fresh(memory);
  try {
    const raw = globalThis.localStorage?.getItem(STORAGE_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : null;
    if (typeof parsed !== "object" || parsed === null) return null;
    const { offsetMs, at } = parsed as Record<string, unknown>;
    return typeof offsetMs === "number" && typeof at === "number" ? fresh({ offsetMs, at }) : null;
  } catch {
    // Private window, blocked storage or a damaged value: start from the device clock.
    return null;
  }
}

function save(offsetMs: number) {
  memory = { offsetMs, at: Date.now() };
  try {
    globalThis.localStorage?.setItem(STORAGE_KEY, JSON.stringify(memory));
  } catch {
    // Full or blocked storage: the in-memory copy still serves this page.
  }
}

/** Forgets the saved offset: `memory` only (as a new page load would), or the stored copy too. For tests. */
export function forgetServerOffset(scope: "memory" | "all" = "all") {
  memory = null;
  if (scope === "memory") return;
  try {
    globalThis.localStorage?.removeItem(STORAGE_KEY);
  } catch {
    // Nothing to forget.
  }
}

export type ServerOffset = {
  /** Server − device clock, in ms (C10). */
  offsetMs: number;
  /** False only while the first `/time` of a device without a saved offset is pending, for at most 1.5 s. */
  settled: boolean;
};

/**
 * Server − device clock, in ms (C10): the QR's 15-second steps and the live clock run on the api's time, so a phone
 * set a minute off still shows a code the gate accepts. One `/time` request when the wallet opens; the midpoint of
 * request and response is the best guess of when the server read its clock.
 *
 * The last offset is kept (in memory and in localStorage, with its time), so the next open shows the code at once on
 * the corrected clock instead of the raw device clock and a jump, and an offline open at the venue (no `/time`) still
 * rotates in step with the gate. Without a saved offset the QR waits for `/time` at most `SERVER_TIME_WAIT_MS`, then
 * uses the device clock as is.
 */
export function useServerOffset(): ServerOffset {
  const [saved] = useState(readSaved);
  const [offset, setOffset] = useState<number | null>(saved?.offsetMs ?? null);
  const [waited, setWaited] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    const sentAt = Date.now();
    const cap = setTimeout(() => setWaited(true), SERVER_TIME_WAIT_MS);
    fetchServerTime(controller.signal).then(
      ({ serverNow }) => {
        if (controller.signal.aborted) return;
        if (!Number.isFinite(serverNow.getTime())) {
          setWaited(true);
          return;
        }
        const value = Math.round(clockOffsetMs(serverNow, (sentAt + Date.now()) / 2));
        save(value);
        setOffset(value);
      },
      () => {
        if (!controller.signal.aborted) setWaited(true);
      },
    );
    return () => {
      controller.abort();
      clearTimeout(cap);
    };
  }, []);

  return { offsetMs: offset ?? 0, settled: offset !== null || waited };
}
