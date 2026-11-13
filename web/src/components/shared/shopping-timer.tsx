"use client";

import { Timer } from "lucide-react";
import Link from "next/link";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { toast } from "sonner";
import { loadClockOffset } from "@/lib/catalog/browser";
import { formatCountdown } from "@/lib/clock";
import { t } from "@/lib/i18n.buyer";
import { ADMISSION_CHANGED_EVENT, loadAdmission } from "@/lib/queue/admission-store";

const subscribe = (notify: () => void) => {
  window.addEventListener(ADMISSION_CHANGED_EVENT, notify);
  window.addEventListener("storage", notify);
  return () => {
    window.removeEventListener(ADMISSION_CHANGED_EVENT, notify);
    window.removeEventListener("storage", notify);
  };
};

// useSyncExternalStore needs a stable snapshot: the raw string, parsed after.
const snapshot = () => {
  try {
    return localStorage.getItem("tr.admission");
  } catch {
    return null;
  }
};

/**
 * The pill on every buyer page showing how long the shopping window lasts (spec 06). It counts down on server time
 * and, when the window ends, disappears with a toast. The admission itself lives in localStorage, shared by all tabs.
 */
export function ShoppingTimer() {
  const raw = useSyncExternalStore(subscribe, snapshot, () => null);
  const [offset, setOffset] = useState<number | null>(null);
  // The clock is only read in effects: reading it while rendering makes Next treat this layout as dynamic.
  const [now, setNow] = useState<number | null>(null);
  const announced = useRef<string | null>(null);

  useEffect(() => {
    let active = true;
    void loadClockOffset().then((value) => {
      if (active) {
        setOffset(value);
        setNow(Date.now());
      }
    });
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 1_000);
    return () => clearInterval(id);
  }, []);

  // `raw` only changes when the stored admission does; loadAdmission also drops one whose window has ended.
  const serverNow = now === null || offset === null ? null : now + offset;
  const admission = raw === null || serverNow === null ? null : loadAdmission(serverNow);
  const left = admission && serverNow !== null ? Math.max(0, Date.parse(admission.expiresAt) - serverNow) : 0;

  useEffect(() => {
    if (raw === null || serverNow === null || admission) {
      return;
    }

    // The stored window has ended: say so once for this admission.
    if (announced.current !== raw) {
      announced.current = raw;
      toast.error(t("queue.timer.expired"));
    }
  }, [admission, serverNow, raw]);

  if (!admission) {
    return null;
  }

  return (
    <div className="fixed inset-x-0 bottom-4 z-50 flex justify-center px-4 pointer-events-none">
      <div className="pointer-events-auto flex items-center gap-3 rounded-full bg-foreground px-4 py-2 text-sm text-background shadow-lg">
        <Timer aria-hidden className="size-4" />
        <span role="timer" aria-label={t("queue.timer.label", "vi", { time: formatCountdown(left) })} className="font-semibold tabular-nums">
          {formatCountdown(left)}
        </span>
        <Link href={`/events/${admission.slug}/seats`} className="font-medium underline underline-offset-4">
          {t("queue.timer.goTo")}
        </Link>
      </div>
    </div>
  );
}
