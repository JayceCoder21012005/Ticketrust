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
import { formatDateTime, formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n.buyer";
import { acceptWaitlistOffer, getWaitlistOffer, type WaitlistOffer } from "@/lib/waitlist/api";

type State =
  | { kind: "loading" }
  | { kind: "ready"; offer: WaitlistOffer; offsetMs: number }
  | { kind: "expired"; resale?: boolean }
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
      (offer) => setState({ kind: "ready", offer, offsetMs: offer.hold ? clockOffsetMs(offer.hold.serverNow, Date.now()) : 0 }),
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
  // A listing's window ending opens it to everyone instead of passing it on: the copy says which.
  const onExpire = useCallback(() => setState((s) => ({ kind: "expired", resale: s.kind === "ready" && Boolean(s.offer.listing) })), []);

  if (state.kind === "loading") return <Skeleton className="h-80 w-full rounded-2xl" />;

  if (state.kind !== "ready") {
    const title = state.kind === "notFound" ? "waitlist.offer.notFound" : state.kind === "expired" ? "waitlist.offer.expired" : "waitlist.offer.loadFailed";
    return (
      <div className="flex flex-col items-start gap-3 rounded-2xl border bg-card p-6">
        <h1 className="text-xl font-bold">{t(title)}</h1>
        {state.kind === "expired" ? (
          <p className="text-sm text-muted-foreground">{t(state.resale ? "resale.offer.expiredHint" : "waitlist.offer.expiredHint")}</p>
        ) : null}
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
  const listing = offer.listing ?? null;
  const buy = async () => {
    setBuying(true);
    setMessage(null);
    try {
      const accepted = await acceptWaitlistOffer(offerId);
      router.push(accepted.orderId ? `/checkout/${accepted.orderId}` : `/events/${accepted.eventSlug}/checkout`);
    } catch (error) {
      if (error instanceof ApiError && error.status === 409 && error.code === "waitlist.offer_expired") setState({ kind: "expired", resale: Boolean(listing) });
      else setMessage(toFriendlyMessage(error));
      setBuying(false);
    }
  };

  return (
    <article className="flex flex-col gap-5 rounded-2xl border bg-card p-6 shadow-sm">
      <header className="flex flex-col gap-1">
        <h1 className="text-brand-gradient text-2xl font-bold tracking-tight">{t("waitlist.offer.title")}</h1>
        <p className="text-sm text-muted-foreground">{t(listing ? "resale.offer.intro" : "waitlist.offer.intro")}</p>
      </header>
      <div>
        <Link href={`/events/${offer.eventSlug}`} className="font-semibold hover:underline">
          {offer.eventTitle}
        </Link>
        <p className="text-sm text-muted-foreground">{formatDateTime(offer.startsAt)}</p>
      </div>
      <HoldCountdown
        expiresAt={offer.expiresAt}
        offsetMs={offsetMs}
        onExpire={onExpire}
        label={t(listing ? "resale.offer.timeLeft" : "waitlist.offer.timeLeft")}
      />
      {offer.hold ? <OrderLines lines={offer.hold} headingId="offer-lines" /> : null}
      {listing ? (
        <dl className="flex items-center justify-between gap-3 rounded-xl border px-4 py-3 text-sm">
          <div>
            <dt className="font-medium">{listing.tierName}</dt>
            <dd className="text-muted-foreground">{listing.seatLabel ? t("resale.accept.seat", "vi", { seat: listing.seatLabel }) : null}</dd>
          </div>
          <dd className="text-base font-semibold">{formatVnd(listing.price)}</dd>
        </dl>
      ) : null}
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
