"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { Price } from "@/components/shared/price";
import { Button } from "@/components/ui/button";
import { FollowButton } from "@/features/follow/follow-button";
import { fetchAvailability, type TimedAvailability } from "@/lib/catalog/browser";
import { computeCtaState, type CtaState } from "@/lib/catalog/cta-state";
import type { EventDetail } from "@/lib/catalog/types";
import { formatOpensIn, remainingMs } from "@/lib/clock";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n";
import { cn } from "@/lib/utils";

/** Live availability is re-read this often while the page is open (sold-out detection). */
const AVAILABILITY_REFRESH_MS = 30_000;

type CtaEvent = Pick<EventDetail, "id" | "slug" | "status" | "endsAt" | "isSoldOut" | "phases">;

type EventCtaProps = {
  event: CtaEvent;
  priceFrom: number | null;
  /** `panel`: the card beside the event info. `bar`: the compact mobile sticky bar. */
  variant?: "panel" | "bar";
  className?: string;
};

// The panel and the mobile bar render together; they share one request per event.
const inflight = new Map<string, Promise<TimedAvailability>>();

function loadAvailability(eventId: string): Promise<TimedAvailability> {
  let request = inflight.get(eventId);
  if (!request) {
    request = fetchAvailability(eventId).finally(() => setTimeout(() => inflight.delete(eventId), 1_000));
    inflight.set(eventId, request);
  }

  return request;
}

/** For tests: forget shared requests between cases. */
export function resetAvailabilityCache() {
  inflight.clear();
}

type LiveState = { availability: TimedAvailability; deviceNow: number };

/**
 * Live availability plus a 1 s clock tick. Both live in one state so every render sees a device time taken
 * after the clock offset was measured; `tick()` forces a re-render exactly at a state boundary.
 */
function useLiveAvailability(eventId: string) {
  const [state, setState] = useState<LiveState | null>(null);

  useEffect(() => {
    let active = true;
    const load = () =>
      loadAvailability(eventId)
        .then((availability) => active && setState({ availability, deviceNow: Date.now() }))
        .catch(() => undefined);

    void load();
    const refresh = setInterval(load, AVAILABILITY_REFRESH_MS);
    const clock = setInterval(() => setState((current) => current && { ...current, deviceNow: Date.now() }), 1_000);
    return () => {
      active = false;
      clearInterval(refresh);
      clearInterval(clock);
    };
  }, [eventId]);

  const tick = () => setState((current) => current && { ...current, deviceNow: Date.now() });
  return { availability: state?.availability ?? null, serverNow: state ? state.deviceNow + state.availability.offsetMs : 0, tick };
}

/**
 * Buy button of the event page (spec 04 CTA states). The state is computed on the client from the sale phases,
 * live availability and the server clock offset (C10), so the page itself stays cacheable and the button flips
 * from countdown to "Vào phòng chờ" at T-30 without a reload, whatever the device clock says.
 */
export function EventCta({ event, priceFrom, variant = "panel", className }: EventCtaProps) {
  const { availability, serverNow, tick } = useLiveAvailability(event.id);

  const state = availability ? computeCtaState(event, serverNow, availability.tiers) : null;

  // The 1 s tick already re-evaluates; this timer makes the switch land exactly on the boundary.
  const nextChangeAt = state?.nextChangeAt;
  useEffect(() => {
    if (nextChangeAt === undefined || !availability) {
      return;
    }

    const delay = Math.max(0, nextChangeAt - (Date.now() + availability.offsetMs)) + 5;
    const id = setTimeout(tick, delay);
    return () => clearTimeout(id);
    // `tick` only calls a state setter; re-arming on its identity would reset the timer every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [nextChangeAt, availability]);

  const price = priceFrom !== null ? <Price amount={priceFrom} from className="text-lg" /> : null;

  if (variant === "bar") {
    return (
      <div className={cn("flex w-full items-center gap-3", className)}>
        <div className="flex min-w-0 flex-1 flex-col">
          {price}
          {state && (state.kind === "Upcoming" || state.kind === "PreQueue") && state.opensAt ? (
            <span className="text-xs text-muted-foreground">
              {t("catalog.cta.opensIn")} <Countdown opensAt={state.opensAt} serverNow={serverNow} />
            </span>
          ) : null}
        </div>
        <CtaAction state={state} slug={event.slug} eventId={event.id} compact />
      </div>
    );
  }

  return (
    <div className={cn("flex flex-col gap-4 rounded-xl border bg-card p-5 shadow-sm", className)}>
      {price ? (
        <div className="flex items-baseline justify-between gap-2">
          <span className="text-sm text-muted-foreground">{t("catalog.cta.prices")}</span>
          {price}
        </div>
      ) : null}
      {state && (state.kind === "Upcoming" || state.kind === "PreQueue") && state.opensAt ? (
        <div className="rounded-lg bg-muted px-4 py-3">
          <p className="text-sm text-muted-foreground">{t("catalog.cta.opensIn")}</p>
          <p className="text-2xl">
            <Countdown opensAt={state.opensAt} serverNow={serverNow} />
          </p>
          <p className="text-xs text-muted-foreground">
            {state.phase?.name} · {formatDateTime(state.opensAt)}
          </p>
        </div>
      ) : null}
      <CtaAction state={state} slug={event.slug} eventId={event.id} />
    </div>
  );
}

function Countdown({ opensAt, serverNow }: { opensAt: string; serverNow: number }) {
  return <span className="font-semibold tabular-nums">{formatOpensIn(remainingMs(opensAt, serverNow, 0))}</span>;
}

const DISABLED: Partial<Record<CtaState["kind"], { label: string; hint?: string }>> = {
  SoldOut: { label: "catalog.cta.soldOut", hint: "catalog.cta.soldOutHint" },
  Ended: { label: "catalog.cta.ended" },
  Cancelled: { label: "catalog.cta.cancelled", hint: "catalog.cta.cancelledHint" },
  SalesClosed: { label: "catalog.cta.salesClosed" },
  NotScheduled: { label: "catalog.cta.notScheduled", hint: "catalog.cta.notScheduledHint" },
};

function CtaAction({ state, slug, eventId, compact = false }: { state: CtaState | null; slug: string; eventId: string; compact?: boolean }) {
  const size = compact ? "default" : "lg";
  const full = compact ? "shrink-0" : "w-full";

  if (!state) {
    return <div aria-hidden className={cn("h-10 animate-pulse rounded-lg bg-muted", compact ? "w-32" : "w-full")} />;
  }

  const queue = `/events/${slug}/queue`;

  switch (state.kind) {
    case "OnSale":
      return (
        <Button asChild size={size} className={full}>
          {/* Instant phases go through the waiting room too: it admits at once and hands out the admission /seats needs. */}
          <Link href={queue} prefetch={false}>
            {t("catalog.cta.buy")}
          </Link>
        </Button>
      );
    case "PreQueue":
      return (
        <div className={cn("flex flex-col gap-2", full)}>
          <Button asChild size={size} className={full}>
            <Link href={queue} prefetch={false}>
              {t("catalog.cta.enterQueue")}
            </Link>
          </Button>
          {compact ? null : <p className="text-xs text-muted-foreground">{t("catalog.cta.queueHint")}</p>}
        </div>
      );
    case "Upcoming":
      return (
        <div className={cn("flex flex-col gap-2", full)}>
          {/* "Nhắc tôi" (spec 09): a login link for visitors, the follow toggle for signed-in ones. */}
          <FollowButton eventId={eventId} returnTo={`/events/${slug}`} size={size} className={full} />
          {compact ? null : <p className="text-xs text-muted-foreground">{t("catalog.cta.remindHint")}</p>}
        </div>
      );
    default: {
      const copy = DISABLED[state.kind]!;
      return (
        <div className={cn("flex flex-col gap-2", full)}>
          <Button variant="secondary" size={size} className={full} disabled>
            {t(copy.label)}
          </Button>
          {copy.hint && !compact ? <p className="text-xs text-muted-foreground">{t(copy.hint)}</p> : null}
        </div>
      );
    }
  }
}
