"use client";

import { useEffect } from "react";

type WakeLockSentinelLike = { release: () => Promise<void> };
type WakeLockNavigator = Navigator & { wakeLock?: { request: (type: "screen") => Promise<WakeLockSentinelLike> } };

/**
 * Keeps the screen on while the scanner is open. The browser drops the lock whenever the tab is hidden, so it is taken
 * again when the tab comes back. Unsupported browsers simply let the screen dim.
 */
export function useWakeLock() {
  useEffect(() => {
    let lock: WakeLockSentinelLike | null = null;
    let active = true;

    const acquire = async () => {
      try {
        const next = await (navigator as WakeLockNavigator).wakeLock?.request("screen");
        if (!next) return;
        if (active) lock = next;
        else void next.release();
      } catch {
        // Refused (low battery, no permission): nothing to do.
      }
    };

    const onVisible = () => {
      if (document.visibilityState === "visible") void acquire();
    };

    void acquire();
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      active = false;
      document.removeEventListener("visibilitychange", onVisible);
      void lock?.release();
    };
  }, []);
}
