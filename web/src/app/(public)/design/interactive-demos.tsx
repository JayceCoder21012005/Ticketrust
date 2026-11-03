"use client";

import { useState } from "react";
import { CountdownTimer } from "@/components/shared/countdown-timer";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";

type Deadline = { expiresAt: string; serverNow: string };

/** Starts on click: reading the clock during prerender would make the page dynamic. */
export function CountdownDemo() {
  const [deadline, setDeadline] = useState<Deadline | null>(null);
  const [expired, setExpired] = useState(false);

  const start = () => {
    const now = Date.now();
    setExpired(false);
    // Pretend the server is 5 minutes ahead of this device: the timer must still start at 01:10.
    setDeadline({
      serverNow: new Date(now + 5 * 60_000).toISOString(),
      expiresAt: new Date(now + 5 * 60_000 + 70_000).toISOString(),
    });
  };

  return (
    <div className="flex flex-wrap items-center gap-3">
      <span>Giữ ghế còn</span>
      {deadline ? (
        <CountdownTimer key={deadline.expiresAt} {...deadline} onExpire={() => setExpired(true)} />
      ) : (
        <span className="font-semibold tabular-nums">--:--</span>
      )}
      <Button size="sm" variant="outline" onClick={start}>
        Bắt đầu
      </Button>
      {expired ? <span className="text-sm text-danger">Hết thời gian giữ ghế</span> : null}
    </div>
  );
}

export function ErrorStateDemo() {
  const [retries, setRetries] = useState(0);

  return (
    <div className="flex flex-col gap-2">
      <ErrorState onRetry={() => setRetries((n) => n + 1)} />
      <p aria-live="polite" className="text-sm text-muted-foreground">
        {retries > 0 ? `Đã thử lại ${retries} lần` : null}
      </p>
    </div>
  );
}
