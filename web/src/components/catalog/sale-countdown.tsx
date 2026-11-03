"use client";

import { useEffect, useState } from "react";
import { loadClockOffset } from "@/lib/catalog/browser";
import { formatOpensIn, remainingMs } from "@/lib/clock";
import { cn } from "@/lib/utils";

type SaleCountdownProps = {
  opensAt: string;
  className?: string;
};

/**
 * "Mở bán sau 2 ngày 5 giờ" on cards. Runs on server time (one shared `/time` request per page) and shows a
 * placeholder until that offset is known, so a wrong device clock never flashes a wrong countdown.
 *
 * The clock is only read in effects: reading it while rendering makes Next treat the surrounding Suspense
 * boundary as dynamic, and the ISR home page would ship skeletons instead of its content.
 */
export function SaleCountdown({ opensAt, className }: SaleCountdownProps) {
  const [serverNow, setServerNow] = useState<number | null>(null);

  useEffect(() => {
    let active = true;
    let id: ReturnType<typeof setInterval> | undefined;
    void loadClockOffset().then((offset) => {
      if (!active) return;
      const tick = () => setServerNow(Date.now() + offset);
      tick();
      id = setInterval(tick, 1000);
    });
    return () => {
      active = false;
      clearInterval(id);
    };
  }, []);

  if (serverNow === null) {
    // Same line box as the text it stands for, so nothing below moves when it fills in (CLS).
    return <span aria-hidden="true" className={cn("inline-block h-[1lh] w-[7ch] rounded bg-muted align-bottom", className)} />;
  }

  const left = remainingMs(opensAt, serverNow, 0);

  return <span className={cn("font-semibold tabular-nums", className)}>{left > 0 ? formatOpensIn(left) : "00:00"}</span>;
}
