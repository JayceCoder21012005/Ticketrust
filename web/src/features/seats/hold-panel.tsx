"use client";

import { AlertDialog } from "radix-ui";
import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { formatCountdown, remainingMs } from "@/lib/clock";
import { formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n";
import type { Hold } from "@/lib/seats/types";

/** Counts down from `expiresAt` on server time (C10) and calls `onExpire` once at zero. */
export function HoldCountdown({ expiresAt, offsetMs, onExpire }: { expiresAt: string; offsetMs: number; onExpire: () => void }) {
  const [now, setNow] = useState<number | null>(null);
  const left = now === null ? null : remainingMs(expiresAt, now, offsetMs);

  useEffect(() => {
    const tick = () => setNow(Date.now());
    const first = setTimeout(tick, 0);
    const timer = setInterval(tick, 1_000);
    return () => {
      clearTimeout(first);
      clearInterval(timer);
    };
  }, []);

  useEffect(() => {
    if (left === 0) onExpire();
  }, [left, onExpire]);

  return (
    <p className="flex items-baseline justify-between rounded-lg bg-muted px-3 py-2 text-sm" role="timer" aria-live="off">
      <span>{t("seats.summary.heldFor")}</span>
      <span className="font-semibold tabular-nums">{left === null ? "--:--" : formatCountdown(left)}</span>
    </p>
  );
}

/** The buyer's hold: what they hold, the countdown, release, and checkout (spec 08) once it exists. */
export function HoldSummary({ hold, offsetMs, onExpire, onRelease }: { hold: Hold; offsetMs: number; onExpire: () => void; onRelease: () => void }) {
  const count = hold.seats.length + hold.ga.reduce((sum, g) => sum + g.quantity, 0);
  return (
    <div className="flex flex-col gap-3">
      <h2 className="text-base font-semibold">{t("seats.summary.held", "vi", { count })}</h2>
      <HoldCountdown expiresAt={hold.expiresAt} offsetMs={offsetMs} onExpire={onExpire} />
      <ul className="flex flex-col gap-1 text-sm">
        {hold.seats.map((s) => (
          <li key={s.index} className="flex justify-between gap-2">
            <span>{`Khu ${s.section} · Hàng ${s.row} · Ghế ${s.number}`}</span>
            <span className="tabular-nums">{formatVnd(s.price)}</span>
          </li>
        ))}
        {hold.ga.map((g) => (
          <li key={g.tierId} className="flex justify-between gap-2">
            <span>{`${g.name} × ${g.quantity}`}</span>
            <span className="tabular-nums">{formatVnd(g.price * g.quantity)}</span>
          </li>
        ))}
      </ul>
      <p className="flex justify-between border-t pt-2 font-semibold">
        <span>{t("seats.summary.total")}</span>
        <span className="tabular-nums">{formatVnd(hold.total)}</span>
      </p>
      <Button disabled>{t("seats.summary.checkoutSoon")}</Button>
      <Button variant="outline" onClick={onRelease}>
        {t("seats.summary.release")}
      </Button>
    </div>
  );
}

/** "Hết thời gian giữ chỗ": the seats went back; the only way on is to choose again. */
export function HoldExpiredDialog({ open, onChooseAgain }: { open: boolean; onChooseAgain: () => void }) {
  return (
    <AlertDialog.Root open={open}>
      <AlertDialog.Portal>
        <AlertDialog.Overlay className="fixed inset-0 z-50 bg-black/50" />
        <AlertDialog.Content className="fixed top-1/2 left-1/2 z-50 grid w-[calc(100%-2rem)] max-w-sm -translate-x-1/2 -translate-y-1/2 gap-3 rounded-xl border bg-background p-6 shadow-lg">
          <AlertDialog.Title className="text-lg font-semibold">{t("seats.expired.title")}</AlertDialog.Title>
          <AlertDialog.Description className="text-sm text-muted-foreground">{t("seats.expired.body")}</AlertDialog.Description>
          <AlertDialog.Action asChild>
            <Button onClick={onChooseAgain}>{t("seats.expired.action")}</Button>
          </AlertDialog.Action>
        </AlertDialog.Content>
      </AlertDialog.Portal>
    </AlertDialog.Root>
  );
}
