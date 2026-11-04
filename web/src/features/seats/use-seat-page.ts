"use client";

import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { toast } from "sonner";
import { getAccessToken, refreshAccessToken } from "@/lib/api/session";
import type { SalePhase, TicketTier, TierRemaining } from "@/lib/catalog/types";
import { clockOffsetMs } from "@/lib/clock";
import { ApiError, toFriendlyMessage } from "@/lib/errors";
import { t } from "@/lib/i18n";
import { loadAdmission } from "@/lib/queue/admission-store";
import { fetchAvailability, fetchSeatMap } from "@/lib/seats/availability";
import { createHold, getMyHold, releaseMyHold, unavailableOf } from "@/lib/seats/holds-api";
import { connectSeatFeed } from "@/lib/seats/realtime";
import { createSeatStore, type SeatStore } from "@/lib/seats/seat-store";
import type { CreateHoldBody, GaLine, Hold, SeatMap } from "@/lib/seats/types";

export type SeatPageEvent = { id: string; slug: string; title: string; tiers: TicketTier[]; phases: SalePhase[] };

const ADMISSION_CODES = new Set(["admission.required", "admission.invalid"]);

const toRemaining = (tiers: TierRemaining[]) => Object.fromEntries(tiers.map((tier) => [tier.tierId, tier.remaining]));

/** The phase on sale now (server time), else the first one: its per-buyer limit applies. */
function currentLimit(phases: SalePhase[], nowMs: number): number {
  const open = phases.find((p) => Date.parse(p.opensAt) <= nowMs && nowMs < Date.parse(p.closesAt));
  return (open ?? phases[0])?.maxTicketsPerUser ?? 1;
}

/**
 * Everything the seat page does (spec 07): loads layout, availability and the buyer's hold, follows the live feed
 * (resyncing on a version gap or reconnect), and turns a selection into a hold. Retrying the same selection reuses its
 * Idempotency-Key (C2); a changed selection gets a new one.
 */
export function useSeatPage(event: SeatPageEvent) {
  const router = useRouter();
  const queuePath = `/events/${event.slug}/queue`;
  const [admission] = useState(() => (typeof window === "undefined" ? null : loadAdmission()));
  // The phase (and its per-buyer limit) is read once per page view; a sale switching phase mid-page is rare.
  const [openedAt] = useState(() => Date.now());
  const usable = admission?.eventId === event.id ? admission : null;
  const [status, setStatus] = useState<"loading" | "ready" | "error">("loading");
  const [loaded, setLoaded] = useState<{ map: SeatMap; store: SeatStore } | null>(null);
  const [reloads, setReloads] = useState(0);
  const [hold, setHold] = useState<Hold | null>(null);
  const [expired, setExpired] = useState(false);
  const [offsetMs, setOffsetMs] = useState(0);
  const [ga, setGa] = useState<Record<string, number>>({});
  const [gaRemaining, setGaRemaining] = useState<Record<string, number>>({});
  const [busy, setBusy] = useState(false);
  const attempt = useRef<{ signature: string; key: string } | null>(null);
  const holdRef = useRef<Hold | null>(null);

  useEffect(() => {
    holdRef.current = hold;
  }, [hold]);

  useEffect(() => {
    if (!usable) router.replace(queuePath);
  }, [usable, router, queuePath]);

  useEffect(() => {
    if (!usable) return;
    let cancelled = false;
    Promise.all([fetchSeatMap(event.id), fetchAvailability(event.id), getMyHold(event.id)])
      .then(([map, availability, mine]) => {
        if (cancelled) return;
        const store = createSeatStore(map.seats.length);
        store.load({ version: availability.version, unavailable: availability.unavailable, mine: mine?.seats.map((s) => s.index) ?? [] });
        setLoaded({ map, store });
        setHold(mine);
        setOffsetMs(availability.offsetMs);
        setGaRemaining(toRemaining(availability.tiers));
        setStatus("ready");
      })
      .catch(() => {
        if (!cancelled) setStatus("error");
      });
    return () => {
      cancelled = true;
    };
  }, [usable, event.id, reloads]);

  const resync = async (store: SeatStore) => {
    const availability = await fetchAvailability(event.id);
    store.load({ version: availability.version, unavailable: availability.unavailable, mine: holdRef.current?.seats.map((s) => s.index) ?? [] });
    setGaRemaining(toRemaining(availability.tiers));
  };

  useEffect(() => {
    if (!loaded) return;
    const { store } = loaded;
    const again = () => void resync(store).catch(() => undefined);
    const feed = connectSeatFeed(event.id, {
      accessToken: async () => getAccessToken() ?? (await refreshAccessToken()) ?? "",
      onResync: again,
      onDelta: (delta) => {
        const result = store.applyDelta(delta);
        if (result.status === "gap") again();
        if (result.status !== "applied") return;
        if (delta.tiers.length > 0) setGaRemaining((previous) => ({ ...previous, ...toRemaining(delta.tiers) }));
        if (result.lost.length > 0) toast.error(t("seats.seatsTaken", "vi", { count: result.lost.length }));
      },
    });
    return () => {
      void feed.stop();
    };
    // resync only reads refs and stable setters.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loaded, event.id]);

  const gaLines = (): GaLine[] => Object.entries(ga).filter(([, q]) => q > 0).map(([tierId, quantity]) => ({ tierId, quantity }));

  const adopt = (store: SeatStore, next: Hold | null) => {
    store.setMine(next?.seats.map((s) => s.index) ?? []);
    store.clearSelection();
    setHold(next);
    setGa({});
    if (next) setOffsetMs(clockOffsetMs(next.serverNow, Date.now()));
  };

  const submit = async (body: CreateHoldBody) => {
    if (!usable || !loaded || busy) return;
    const signature = JSON.stringify(body);
    if (attempt.current?.signature !== signature) attempt.current = { signature, key: crypto.randomUUID() };
    setBusy(true);
    try {
      adopt(loaded.store, await createHold(event.id, body, usable.token, attempt.current.key));
      attempt.current = null;
    } catch (error) {
      await handleFailure(loaded.store, error);
    } finally {
      setBusy(false);
    }
  };

  const handleFailure = async (store: SeatStore, error: unknown) => {
    const taken = unavailableOf(error);
    if (taken.length > 0) {
      store.markTaken(taken);
      attempt.current = null;
      toast.error(t("seats.seatsTaken", "vi", { count: taken.length }));
    } else if (error instanceof ApiError && ADMISSION_CODES.has(error.code ?? "")) {
      router.replace(queuePath);
    } else if (error instanceof ApiError && error.code === "hold.already_exists") {
      adopt(store, await getMyHold(event.id).catch(() => null));
    } else {
      toast.error(toFriendlyMessage(error));
    }
  };

  const backToSelection = async () => {
    if (!loaded) return;
    adopt(loaded.store, null);
    setExpired(false);
    await resync(loaded.store).catch(() => undefined);
  };

  return {
    status,
    map: loaded?.map ?? null,
    store: loaded?.store ?? null,
    hold,
    expired,
    offsetMs,
    busy,
    ga,
    gaRemaining,
    limit: currentLimit(event.phases, openedAt + offsetMs),
    setGaQuantity: (tierId: string, quantity: number) => setGa((previous) => ({ ...previous, [tierId]: quantity })),
    retry: () => {
      setStatus("loading");
      setReloads((n) => n + 1);
    },
    continueWithSelection: () => submit({ seatIndexes: loaded?.store.selected() ?? [], ga: gaLines(), replaceExisting: false }),
    bestAvailable: (tierId: string, quantity: number) => submit({ bestAvailable: { tierId, quantity }, ga: gaLines(), replaceExisting: false }),
    release: async () => {
      try {
        await releaseMyHold(event.id);
        await backToSelection();
      } catch (error) {
        toast.error(toFriendlyMessage(error));
      }
    },
    expire: () => setExpired(true),
    backToSelection,
  };
}
