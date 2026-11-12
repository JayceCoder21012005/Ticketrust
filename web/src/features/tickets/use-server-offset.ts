"use client";

import { useEffect, useState } from "react";
import { clockOffsetMs } from "@/lib/clock";
import { fetchServerTime } from "@/lib/server-time";

/**
 * Server − device clock, in ms (C10): the QR's 15-second steps and the live clock run on the api's time, so a phone
 * set a minute off still shows a code the gate accepts. One `/time` request when the wallet opens; the midpoint of
 * request and response is the best guess of when the server read its clock. Until it answers, or when it cannot
 * (offline at the venue), the device clock is used as is.
 */
export function useServerOffset(): number {
  const [offset, setOffset] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const sentAt = Date.now();
    fetchServerTime(controller.signal).then(
      ({ serverNow }) => {
        if (!controller.signal.aborted && Number.isFinite(serverNow.getTime())) {
          setOffset(Math.round(clockOffsetMs(serverNow, (sentAt + Date.now()) / 2)));
        }
      },
      () => undefined,
    );
    return () => controller.abort();
  }, []);

  return offset;
}
