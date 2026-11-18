"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { ErrorState } from "@/components/shared/error-state";
import { Button } from "@/components/ui/button";
import { Drawer, DrawerContent, DrawerTitle, DrawerTrigger } from "@/components/ui/drawer";
import { Skeleton } from "@/components/ui/skeleton";
import { t } from "@/lib/i18n.buyer";
import { HoldExpiredDialog, HoldSummary } from "./hold-panel";
import { LazySeatMap } from "./lazy-seat-map";
import { PANEL_TITLE_SELECTOR, SeatLegend, SelectionPanel } from "./selection-panel";
import { useSeatPage, type SeatPageEvent } from "./use-seat-page";

/**
 * `/events/[slug]/seats` (spec 07): map on the left/top, summary on the right on desktop and in a bottom drawer on
 * phones. Needs this event's admission from the waiting room; without one the buyer goes back to the queue.
 */
export function SeatsView({ event }: { event: SeatPageEvent }) {
  const page = useSeatPage(event);
  const [filter, setFilter] = useState<Set<string>>(() => new Set());
  const { expire } = page;
  const onExpire = useCallback(() => expire(), [expire]);

  // Starting or ending a hold swaps the panel and unmounts the button that had focus: put it on the new heading.
  // The panel is rendered twice (desktop aside, mobile drawer): focus the heading of the one the buyer is looking at.
  const [drawerOpen, setDrawerOpen] = useState(false);
  const asidePanel = useRef<HTMLDivElement>(null);
  const drawerPanel = useRef<HTMLDivElement>(null);
  const held = page.hold !== null;
  const wasHeld = useRef<boolean | null>(null);
  useEffect(() => {
    if (page.status !== "ready") return;
    if (wasHeld.current !== null && wasHeld.current !== held) (drawerOpen ? drawerPanel : asidePanel).current?.querySelector<HTMLElement>(PANEL_TITLE_SELECTOR)?.focus();
    wasHeld.current = held;
    // Opening or closing the drawer alone must not move focus.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [held, page.status]);

  if (page.status === "error") {
    return (
      <div className="mx-auto w-full max-w-6xl px-4 py-10">
        <ErrorState title={t("seats.loadFailed")} onRetry={page.retry} />
      </div>
    );
  }

  if (page.status === "loading" || !page.map || !page.store) {
    return (
      <div className="mx-auto grid w-full max-w-6xl gap-6 px-4 py-6 md:grid-cols-[1fr_22rem]">
        <Skeleton className="aspect-[10/7] w-full rounded-xl" />
        <Skeleton className="h-72 w-full rounded-xl" />
      </div>
    );
  }

  const { map, store, hold } = page;
  const toggleTier = (tierId: string) =>
    setFilter((previous) => {
      const next = new Set(previous);
      if (!next.delete(tierId)) next.add(tierId);
      return next;
    });

  const panel = hold ? (
    <HoldSummary hold={hold} offsetMs={page.offsetMs} checkoutHref={`/events/${event.slug}/checkout`} onExpire={onExpire} onRelease={() => void page.release()} />
  ) : (
    <SelectionPanel
      map={map}
      store={store}
      tiers={event.tiers}
      limit={page.limit}
      ga={page.ga}
      gaRemaining={page.gaRemaining}
      busy={page.busy}
      onGaChange={page.setGaQuantity}
      onBestAvailable={(tierId, quantity) => void page.bestAvailable(tierId, quantity)}
      onContinue={() => void page.continueWithSelection()}
    />
  );

  return (
    <div className="mx-auto grid w-full max-w-6xl gap-6 px-4 py-6 pb-24 md:grid-cols-[1fr_22rem] md:pb-6">
      <section className="flex min-w-0 flex-col gap-4" aria-labelledby="seats-title">
        <h1 id="seats-title" className="text-xl font-semibold">
          {`${t("seats.title")} · ${event.title}`}
        </h1>
        {map.seats.length > 0 ? <SeatLegend tiers={event.tiers} filter={filter} onToggle={toggleTier} /> : null}
        {map.seats.length > 0 ? (
          <LazySeatMap seatMap={map} tiers={event.tiers} store={store} tierFilter={filter} onToggle={(index: number) => store.toggle(index)} />
        ) : null}
      </section>
      <aside className="hidden md:block">
        <div ref={asidePanel} className="sticky top-20 rounded-xl border bg-card p-4">
          {panel}
        </div>
      </aside>
      <div className="fixed inset-x-0 bottom-0 z-40 border-t bg-background/95 p-3 md:hidden">
        <Drawer open={drawerOpen} onOpenChange={setDrawerOpen}>
          <DrawerTrigger asChild>
            <Button className="w-full" variant="secondary">
              {t("seats.summary.open")}
            </Button>
          </DrawerTrigger>
          <DrawerContent className="p-4">
            <DrawerTitle className="sr-only">{t("seats.summary.title")}</DrawerTitle>
            <div ref={drawerPanel}>{panel}</div>
          </DrawerContent>
        </Drawer>
      </div>
      <HoldExpiredDialog open={page.expired} onChooseAgain={() => void page.backToSelection()} />
    </div>
  );
}
