"use client";

import { useEffect, useRef } from "react";
import { formatPosition } from "@/lib/queue/format";
import { t } from "@/lib/i18n.buyer";

/** "(#1.234) TicketRush" in the tab, so the place is visible from another tab. The old title comes back on exit. */
export function useTabTitle(position: number | null): void {
  useEffect(() => {
    if (position === null) {
      return;
    }

    const previous = document.title;
    document.title = `(${formatPosition(position)}) TicketRush`;
    return () => {
      document.title = previous;
    };
  }, [position]);
}

/** A short two-note chime from the Web Audio API (no audio file to ship). Browsers may block it: callers catch. */
export function playChime(): void {
  const AudioCtx = window.AudioContext;
  const context = new AudioCtx();
  const start = context.currentTime;
  [660, 880].forEach((frequency, index) => {
    const oscillator = context.createOscillator();
    const gain = context.createGain();
    oscillator.frequency.value = frequency;
    gain.gain.setValueAtTime(0.0001, start + index * 0.18);
    gain.gain.exponentialRampToValueAtTime(0.2, start + index * 0.18 + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, start + index * 0.18 + 0.4);
    oscillator.connect(gain).connect(context.destination);
    oscillator.start(start + index * 0.18);
    oscillator.stop(start + index * 0.18 + 0.45);
  });
  setTimeout(() => void context.close(), 1_200);
}

type AlertOptions = { admitted: boolean; eventTitle: string; chime?: () => void };

/**
 * When the buyer's turn comes: tab title, chime and (if allowed and the tab is hidden) a browser notification.
 * Every part is best effort: blocked audio or a missing Notification API must never break the page.
 */
export function useAdmittedAlerts({ admitted, eventTitle, chime = playChime }: AlertOptions): void {
  const alerted = useRef(false);

  useEffect(() => {
    if (!admitted || alerted.current) {
      return;
    }

    alerted.current = true;
    const title = t("queue.admitted.title");
    document.title = `${title} — TicketRush`;

    try {
      chime();
    } catch {
      // Autoplay policy: the visual state is enough.
    }

    try {
      if (typeof Notification !== "undefined" && Notification.permission === "granted" && document.visibilityState === "hidden") {
        new Notification(title, { body: t("queue.admitted.notificationBody", "vi", { event: eventTitle }) });
      }
    } catch {
      // Some mobile browsers throw on the constructor.
    }
  }, [admitted, chime, eventTitle]);
}

type WakeLockSentinelLike = { release: () => Promise<void> };

/** Keeps the screen on while waiting (mobile): a sleeping phone stops polling and loses the chance to react. */
export function useWakeLock(enabled: boolean): void {
  useEffect(() => {
    const wakeLock = (navigator as unknown as { wakeLock?: { request: (type: "screen") => Promise<WakeLockSentinelLike> } }).wakeLock;
    if (!enabled || !wakeLock) {
      return;
    }

    let sentinel: WakeLockSentinelLike | null = null;
    let cancelled = false;
    const acquire = async () => {
      try {
        const next = await wakeLock.request("screen");
        if (cancelled) {
          await next.release();
        } else {
          sentinel = next;
        }
      } catch {
        // Denied (battery saver, not visible): waiting still works.
      }
    };

    void acquire();
    // The browser drops the lock when the tab is hidden; take it again when the buyer comes back.
    const onVisible = () => document.visibilityState === "visible" && void acquire();
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      cancelled = true;
      document.removeEventListener("visibilitychange", onVisible);
      void sentinel?.release();
    };
  }, [enabled]);
}
