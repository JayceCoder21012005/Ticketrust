"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { OrderLines } from "@/features/checkout/order-lines";
import { HoldCountdown } from "@/features/seats/hold-panel";
import { clockOffsetMs } from "@/lib/clock";
import { ApiError, toFriendlyMessage } from "@/lib/errors";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n.buyer";
import { acceptWaitlistOffer, getWaitlistOffer, type WaitlistOffer } from "@/lib/waitlist/api";

type State =
  | { kind: "loading" }
  | { kind: "ready"; offer: WaitlistOffer; offsetMs: number }
  | { kind: "expired" }
  | { kind: "notFound" }
  | { kind: "failed" };

/**
 * The offer page (spec 13): the tickets held from the waitlist, priced, a countdown to when they pass to the next in line,
 * and "Mua ngay", which confirms the offer is still live and continues in checkout from that hold (spec 08).
 */
export function OfferView({ offerId }: { offerId: string }) {
  const router = useRouter();
  const [state, setState] = useState<State>({ kind: "loading" });
  const [buying, setBuying] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  const load = useCallback(() => {
    const controller = new AbortController();
    getWaitlistOffer(offerId, controller.signal).then(
      (offer) => setState({ kind: "ready", offer, offsetMs: clockOffsetMs(offer.hold.serverNow, Date.now()) }),
      (error: unknown) => {
        if (controller.signal.aborted) return;
        if (error instanceof ApiError && error.status === 404) setState({ kind: "notFound" });
        else if (error instanceof ApiError && error.status === 409) setState({ kind: "expired" });
        else setState({ kind: "failed" });
      },
    );
    return () => controller.abort();
  }, [offerId]);

  useEffect(() => load(), [load]);
  const onExpire = useCallback(() => setState({ kind: "expired" }), []);

  if (state.kind === "loading") return <Skeleton className="h-80 w-full rounded-2xl" />;

  if (state.kind !== "ready") {
    const title = state.kind === "notFound" ? "waitlist.offer.notFound" : state.kind === "expired" ? "waitlist.offer.expired" : "waitlist.offer.loadFailed";
    return (
      <div className="flex flex-col items-start gap-3 rounded-2xl border bg-card p-6">
        <h1 className="text-xl font-bold">{t(title)}</h1>
        {state.kind === "expired" ? <p className="text-sm text-muted-foreground">{t("waitlist.offer.expiredHint")}</p> : null}
        {state.kind === "failed" ? (
          <Button type="button" variant="outline" onClick={() => { setState({ kind: "loading" }); load(); }}>
            {t("common.retry")}
          </Button>
        ) : null}
        <Button asChild variant="link" className="px-0">
          <Link href="/account/waitlist">{t("waitlist.offer.toWaitlist")}</Link>
        </Button>
      </div>
    );
  }

  const { offer, offsetMs } = state;
  const buy = async () => {
    setBuying(true);
    setMessage(null);
    try {
      const accepted = await acceptWaitlistOffer(offerId);
      router.push(`/events/${accepted.eventSlug}/checkout`);
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) setState({ kind: "expired" });
      else setMessage(toFriendlyMessage(error));
      setBuying(false);
    }
  };

  return (
    <article className="flex flex-col gap-5 rounded-2xl border bg-card p-6 shadow-sm">
      <header className="flex flex-col gap-1">
        <h1 className="text-brand-gradient text-2xl font-bold tracking-tight">{t("waitlist.offer.title")}</h1>
        <p className="text-sm text-muted-foreground">{t("waitlist.offer.intro")}</p>
      </header>
      <div>
        <Link href={`/events/${offer.eventSlug}`} className="font-semibold hover:underline">
          {offer.eventTitle}
        </Link>
        <p className="text-sm text-muted-foreground">{formatDateTime(offer.startsAt)}</p>
      </div>
      <HoldCountdown expiresAt={offer.expiresAt} offsetMs={offsetMs} onExpire={onExpire} label={t("waitlist.offer.timeLeft")} />
      <OrderLines lines={offer.hold} headingId="offer-lines" />
      {message ? (
        <p role="alert" className="text-sm text-danger">
          {message}
        </p>
      ) : null}
      <Button type="button" size="lg" onClick={buy} disabled={buying}>
        {t("waitlist.offer.buy")}
      </Button>
    </article>
  );
}
