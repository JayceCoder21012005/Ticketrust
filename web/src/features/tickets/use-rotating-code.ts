"use client";

import { useEffect, useMemo, useState } from "react";
import { base64ToBytes, currentStep, msUntilNextStep, qrPayload } from "@/lib/tickets/qr-code";

/** `insecure`: no Web Crypto (an http page that is not localhost); `failed`: the code could not be computed. */
export type RotatingCodeError = "insecure" | "failed";

export type RotatingCode = {
  /** The code to draw, or null until the first one is computed (or on error). */
  payload: string | null;
  /** Milliseconds until the next code, on the server-corrected clock. */
  msLeft: number;
  /** The step of the current time (the payload catches up with it a few milliseconds later). */
  step: number;
  /** Server-corrected time of the last tick, for the live clock. */
  nowMs: number;
  error: RotatingCodeError | null;
};

const hasWebCrypto = () => typeof globalThis.crypto !== "undefined" && typeof globalThis.crypto.subtle !== "undefined";

const stepOf = (nowMs: number) => currentStep(Math.floor(nowMs / 1000));

/**
 * The device clock, ticking on every whole second of the server clock (`offsetMs` = server − device, C10): one
 * `setTimeout` per second boundary, re-aimed each time, so it never drifts like an interval. Stops while the tab is
 * hidden and catches up the moment it is visible again.
 */
function useSecondTicker(offsetMs: number): number {
  const [deviceNow, setDeviceNow] = useState(() => Date.now());

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;

    const schedule = () => {
      clearTimeout(timer);
      const serverNow = Date.now() + offsetMs;
      timer = setTimeout(tick, 1000 - (((serverNow % 1000) + 1000) % 1000));
    };
    function tick() {
      setDeviceNow(Date.now());
      schedule();
    }
    const onVisibility = () => {
      if (document.visibilityState === "visible") {
        tick();
      } else {
        clearTimeout(timer);
      }
    };

    if (document.visibilityState === "visible") schedule();
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [offsetMs]);

  return deviceNow + offsetMs;
}

type Generated = { step: number; payload: string | null; error: RotatingCodeError | null };

/**
 * The ticket's rotating code (spec 10): recomputed with Web Crypto exactly when a 15-second step starts on the
 * server-corrected clock. A slow computation that finishes after the next step started is dropped, so an older code
 * never replaces a newer one. The secret stays in memory: it is never rendered, cached or logged.
 */
export function useRotatingCode(ticketId: string, secret: string | null, offsetMs = 0): RotatingCode {
  const nowMs = useSecondTicker(offsetMs);
  const step = stepOf(nowMs);
  const secure = hasWebCrypto();
  const [generated, setGenerated] = useState<Generated | null>(null);

  const secretBytes = useMemo(() => {
    if (!secret) return null;
    try {
      return base64ToBytes(secret);
    } catch {
      return "invalid" as const;
    }
  }, [secret]);

  useEffect(() => {
    if (!secretBytes || !secure) return;

    let current = true;
    const unixSeconds = step * 15;
    const result =
      secretBytes === "invalid" ? Promise.reject(new RangeError("secret is not base64.")) : qrPayload(ticketId, secretBytes, unixSeconds);
    result.then(
      (payload) => current && setGenerated({ step, payload, error: null }),
      () => current && setGenerated({ step, payload: null, error: "failed" }),
    );
    // The next step's effect runs this first: a late answer for this step is ignored.
    return () => {
      current = false;
    };
  }, [ticketId, secretBytes, secure, step]);

  if (!secret) return { payload: null, msLeft: msUntilNextStep(nowMs), step, nowMs, error: null };
  if (!secure) return { payload: null, msLeft: msUntilNextStep(nowMs), step, nowMs, error: "insecure" };

  return { payload: generated?.payload ?? null, msLeft: msUntilNextStep(nowMs), step, nowMs, error: generated?.error ?? null };
}
