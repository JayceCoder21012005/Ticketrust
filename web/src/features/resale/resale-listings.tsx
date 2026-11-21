"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState, useSyncExternalStore } from "react";
import { Button } from "@/components/ui/button";
import { loginHref } from "@/features/follow/follow-ui";
import { getSession, refreshAccessToken } from "@/lib/api/session";
import { hasSessionHint } from "@/lib/auth/session-hint";
import { toFriendlyMessage } from "@/lib/errors";
import { formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n";
import { listEventListings, reserveListing, type ListingTierGroup } from "@/lib/resale/api";

const noSubscription = () => () => {};

/**
 * "Vé bán lại" (spec 14) under a sold-out event's call to action: active listings past their waitlist window, by tier, cheapest
 * first. Renders nothing while loading, on error or when there are none. "Mua" reserves the listing for 10 minutes and continues
 * in the 08 checkout; a visitor without a session goes to login and comes back here.
 */
export default function ResaleListings({ eventId, slug }: { eventId: string; slug: string }) {
  const router = useRouter();
  const signedIn = useSyncExternalStore(noSubscription, () => hasSessionHint(document.cookie), () => false);
  const [groups, setGroups] = useState<ListingTierGroup[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    listEventListings(eventId, controller.signal).then(setGroups, () => undefined);
    return () => controller.abort();
  }, [eventId]);

  if (groups.length === 0) return null;

  const buy = async (listingId: string) => {
    setBusy(listingId);
    setMessage(null);
    try {
      if (!getSession()) await refreshAccessToken();
      const order = await reserveListing(listingId, crypto.randomUUID());
      router.push(`/checkout/${order.id}`);
    } catch (error) {
      setMessage(toFriendlyMessage(error));
      setBusy(null);
      void listEventListings(eventId).then(setGroups, () => undefined);
    }
  };

  return (
    <section aria-labelledby="resale-title" className="flex w-full flex-col gap-3 rounded-xl border p-4">
      <div>
        <h2 id="resale-title" className="text-base font-semibold">
          {t("catalog.resale.title")}
        </h2>
        <p className="text-xs text-muted-foreground">{t("catalog.resale.intro")}</p>
      </div>
      {groups.map((group) => (
        <div key={group.tierId} className="flex flex-col gap-1.5">
          <h3 className="flex items-baseline justify-between gap-2 text-sm font-medium">
            <span className="flex items-center gap-2">
              <span aria-hidden className="size-2.5 rounded-full" style={{ backgroundColor: group.color }} />
              {group.tierName}
            </span>
            <span className="text-xs font-normal text-muted-foreground">{t("catalog.resale.faceValue", "vi", { price: formatVnd(Number(group.faceValue)) })}</span>
          </h3>
          <ul className="flex flex-col divide-y rounded-lg border">
            {group.listings.map((listing) => (
              <li key={listing.listingId} className="flex items-center justify-between gap-3 px-3 py-2 text-sm">
                <span className="min-w-0 truncate">
                  {listing.seatLabel ? t("catalog.resale.seat", "vi", { seat: listing.seatLabel }) : t("catalog.resale.ga")}
                </span>
                <span className="flex items-center gap-3">
                  <span className="font-semibold tabular-nums">{formatVnd(Number(listing.price))}</span>
                  {signedIn ? (
                    <Button type="button" size="sm" onClick={() => void buy(listing.listingId)} disabled={busy !== null}>
                      {t("catalog.resale.buy")}
                    </Button>
                  ) : (
                    <Button asChild size="sm">
                      <Link href={loginHref(`/events/${slug}`)} prefetch={false}>
                        {t("catalog.resale.buy")}
                      </Link>
                    </Button>
                  )}
                </span>
              </li>
            ))}
          </ul>
        </div>
      ))}
      {message ? (
        <p role="alert" className="text-sm text-danger">
          {message}
        </p>
      ) : null}
    </section>
  );
}
