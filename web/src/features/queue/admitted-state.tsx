"use client";

import { PartyPopper } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { t } from "@/lib/i18n.buyer";
import type { QueueStatus } from "@/lib/queue/types";

const REDIRECT_SECONDS = 3;

/** The buyer's turn: celebrate, then go to seat selection by itself after 3 s (the button is there to go sooner). */
export function AdmittedState({ status, slug }: { status: QueueStatus; slug: string }) {
  const { push } = useRouter();
  const href = `/events/${slug}/seats`;
  const [secondsLeft, setSecondsLeft] = useState(REDIRECT_SECONDS);
  const minutes = status.tokenExpiresAt
    ? Math.max(1, Math.round((Date.parse(status.tokenExpiresAt) - Date.parse(status.serverNow)) / 60_000))
    : 10;

  useEffect(() => {
    const tick = setInterval(() => setSecondsLeft((left) => Math.max(0, left - 1)), 1_000);
    const go = setTimeout(() => push(href), REDIRECT_SECONDS * 1_000);
    return () => {
      clearInterval(tick);
      clearTimeout(go);
    };
  }, [href, push]);

  return (
    <div className="flex flex-col items-center gap-5 text-center">
      <PartyPopper aria-hidden className="size-10 text-success" />
      <div className="flex flex-col gap-1">
        <h2 className="text-2xl font-bold">{t("queue.admitted.title")}</h2>
        <p className="text-sm text-muted-foreground">{t("queue.admitted.description", "vi", { minutes })}</p>
      </div>
      <Button asChild size="lg" className="w-full">
        <Link href={href}>{t("queue.admitted.go")}</Link>
      </Button>
      <p className="text-xs text-muted-foreground" aria-live="off">
        {t("queue.admitted.redirect", "vi", { seconds: secondsLeft })}
      </p>
    </div>
  );
}
