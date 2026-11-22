"use client";

import { useEffect, useMemo, useState } from "react";
import { SeatMap } from "@/features/seats/seat-map/seat-map";
import { getAccessToken, refreshAccessToken } from "@/lib/api/session";
import type { TicketTier } from "@/lib/catalog/types";
import type { DashboardTier } from "@/lib/dashboard/api";
import { fetchAvailability, fetchSeatMap } from "@/lib/seats/availability";
import { connectSeatFeed } from "@/lib/seats/realtime";
import { createSeatStore, type SeatStore } from "@/lib/seats/seat-store";
import type { SeatMap as SeatMapData } from "@/lib/seats/types";

const noTierFilter = new Set<string>();
const ignore = () => undefined;

/**
 * The live seat map of spec 07 in read-only mode: free, held and sold seats as buyers see them, following the same seat feed.
 * Nothing for an event without seats (general admission only).
 */
export function SeatHeatmap({ eventId, tiers }: { eventId: string; tiers: DashboardTier[] }) {
  const [loaded, setLoaded] = useState<{ map: SeatMapData; store: SeatStore } | null | "none">(null);

  useEffect(() => {
    let live = true;
    Promise.all([fetchSeatMap(eventId), fetchAvailability(eventId)]).then(
      ([map, availability]) => {
        if (!live) return;
        if (map.seats.length === 0) return setLoaded("none");
        const store = createSeatStore(map.seats.length);
        store.load({ version: availability.version, unavailable: availability.unavailable, mine: [] });
        setLoaded({ map, store });
      },
      () => live && setLoaded("none"),
    );
    return () => {
      live = false;
    };
  }, [eventId]);

  useEffect(() => {
    if (!loaded || loaded === "none") return;
    const { store } = loaded;
    const resync = () =>
      void fetchAvailability(eventId).then((a) => store.load({ version: a.version, unavailable: a.unavailable, mine: [] }), ignore);
    const feed = connectSeatFeed(eventId, {
      accessToken: async () => getAccessToken() ?? (await refreshAccessToken()) ?? "",
      onResync: resync,
      onDelta: (delta) => {
        if (store.applyDelta(delta).status === "gap") resync();
      },
    });
    return () => {
      void feed.stop();
    };
  }, [loaded, eventId]);

  const catalogTiers = useMemo<TicketTier[]>(
    () => tiers.map((t) => ({ id: t.tierId, name: t.name, price: t.price, currency: "VND", kind: "Seated", color: t.color })),
    [tiers],
  );

  if (loaded === "none") return <p className="text-sm text-muted-foreground">Sự kiện này không có sơ đồ ghế.</p>;
  if (!loaded) return null;
  return <SeatMap seatMap={loaded.map} tiers={catalogTiers} store={loaded.store} tierFilter={noTierFilter} onToggle={ignore} />;
}
