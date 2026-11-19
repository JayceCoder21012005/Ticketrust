"use client";

import { CalendarDays, Hourglass, MapPin } from "lucide-react";
import Link from "next/link";
import { useEffect, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n.buyer";
import { listMyWaitlist, type MyWaitlistItem } from "@/lib/waitlist/api";

type Loaded = { state: "loading" } | { state: "ready"; items: MyWaitlistItem[] } | { state: "failed" };

/** `/account/waitlist` (spec 13): each live entry with its place in line, or the way to its offer. */
export function MyWaitlist() {
  const [loaded, setLoaded] = useState<Loaded>({ state: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    listMyWaitlist(controller.signal).then(
      (items) => !controller.signal.aborted && setLoaded({ state: "ready", items }),
      () => !controller.signal.aborted && setLoaded({ state: "failed" }),
    );
    return () => controller.abort();
  }, [attempt]);

  return (
    <section className="flex flex-col gap-6" aria-labelledby="waitlist-title">
      <header className="flex flex-col gap-1">
        <h1 id="waitlist-title" className="text-2xl font-bold tracking-tight">
          {t("waitlist.account.title")}
        </h1>
        <p className="text-sm text-muted-foreground">{t("waitlist.account.intro")}</p>
      </header>

      {loaded.state === "loading" ? (
        <Skeleton className="h-28 w-full rounded-xl" />
      ) : loaded.state === "failed" ? (
        <p role="alert" className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
          {t("waitlist.account.loadFailed")}
          <Button type="button" variant="outline" size="sm" onClick={() => { setLoaded({ state: "loading" }); setAttempt((a) => a + 1); }}>
            {t("common.retry")}
          </Button>
        </p>
      ) : loaded.items.length === 0 ? (
        <div className="flex flex-col items-start gap-1 rounded-xl border border-dashed p-6">
          <p className="font-medium">{t("waitlist.account.empty")}</p>
          <p className="text-sm text-muted-foreground">{t("waitlist.account.emptyHint")}</p>
        </div>
      ) : (
        <ul className="flex flex-col gap-3">
          {loaded.items.map((item) => (
            <li key={item.entry.entryId} className="flex flex-col gap-3 rounded-xl border bg-card p-4 sm:flex-row sm:items-center sm:justify-between">
              <div className="flex min-w-0 flex-col gap-1">
                <Link href={`/events/${item.eventSlug}`} className="truncate font-semibold hover:underline">
                  {item.eventTitle}
                </Link>
                <span className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
                  <span className="flex items-center gap-1">
                    <CalendarDays aria-hidden className="size-3.5" />
                    {formatDateTime(item.startsAt)}
                  </span>
                  <span className="flex items-center gap-1">
                    <MapPin aria-hidden className="size-3.5" />
                    {item.city}
                  </span>
                  <span>
                    {item.tierName ?? t("waitlist.account.anyTier")} · {t("waitlist.account.quantity", "vi", { quantity: item.entry.quantity })}
                  </span>
                </span>
              </div>
              {item.entry.status === "Offered" && item.entry.offer ? (
                <Button asChild size="sm" className="shrink-0">
                  <Link href={`/waitlist/offers/${item.entry.offer.offerId}`}>{t("waitlist.account.openOffer")}</Link>
                </Button>
              ) : (
                <Badge variant="secondary" className="shrink-0 gap-1">
                  <Hourglass aria-hidden className="size-3" />
                  {item.entry.position ? t("waitlist.position", "vi", { position: item.entry.position }) : t("waitlist.waiting")}
                </Badge>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
