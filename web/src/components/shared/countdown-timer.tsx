"use client";

import { useEffect, useRef, useState } from "react";
import { clockOffsetMs, formatCountdown, remainingMs } from "@/lib/clock";
import { cn } from "@/lib/utils";

const ANNOUNCE_THRESHOLD_MS = 60_000;

type CountdownTimerProps = {
  /** Deadline from the api. */
  expiresAt: string;
  /** Server clock sent alongside `expiresAt`; corrects device clock skew (C10). */
  serverNow: string;
  onExpire?: () => void;
  className?: string;
};

export function CountdownTimer({ expiresAt, serverNow, onExpire, className }: CountdownTimerProps) {
  const [offset] = useState(() => clockOffsetMs(serverNow, Date.now()));
  const [left, setLeft] = useState(() => remainingMs(expiresAt, Date.now(), offset));
  const expired = useRef(false);

  useEffect(() => {
    const tick = () => {
      const next = remainingMs(expiresAt, Date.now(), offset);
      setLeft(next);
      if (next === 0 && !expired.current) {
        expired.current = true;
        onExpire?.();
      }
    };

    tick();
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  }, [expiresAt, offset, onExpire]);

  const urgent = left <= ANNOUNCE_THRESHOLD_MS;

  return (
    <span
      role="timer"
      // Announcing every second for 10 minutes would drown a screen reader; only the final minute matters.
      aria-live={urgent ? "polite" : "off"}
      aria-atomic="true"
      className={cn("font-semibold tabular-nums", urgent && "text-destructive", className)}
    >
      {formatCountdown(left)}
    </span>
  );
}
