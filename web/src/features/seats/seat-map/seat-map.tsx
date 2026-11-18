"use client";

import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent } from "react";
import { Minus, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { TicketTier } from "@/lib/catalog/types";
import { t } from "@/lib/i18n.buyer";
import type { SeatStore } from "@/lib/seats/seat-store";
import type { SeatInfo, SeatMap as SeatMapData } from "@/lib/seats/types";
import { useStoreRevision } from "@/lib/seats/use-seat";
import { nextSeat, SEAT_KEYS, type SeatKey } from "./keyboard";
import { boundsOf, overviewLayout, type Box, type OverviewLayout } from "./overview-layout";
import { Seat } from "./seat";
import { usePanZoom } from "./use-pan-zoom";

const PAD = 30;
/** The map keeps the venue's shape, within these limits (width / height): a long thin hall still gets a usable height. */
const MIN_ASPECT = 4 / 3;
const MAX_ASPECT = 21 / 9;

type SeatMapProps = {
  seatMap: SeatMapData;
  tiers: TicketTier[];
  store: SeatStore;
  /** Tier ids to highlight; empty = all. Other seats are dimmed, never hidden. */
  tierFilter: Set<string>;
  onToggle: (index: number) => void;
};

const toViewBox = (box: Box) => `${box.x} ${box.y} ${box.width} ${box.height}`;

/**
 * Seat map of spec 07 (dynamically imported by the page). Overview of sections first; opening a section shows its seats
 * with wheel/pinch zoom and drag. One delegated click/keydown handler on the SVG; seats are memoized and subscribe to
 * their own index (spike S1: p95 16.7 ms per frame with 10,000 seats).
 */
export function SeatMap({ seatMap, tiers, store, tierFilter, onToggle }: SeatMapProps) {
  const svg = useRef<SVGSVGElement>(null);
  const group = useRef<SVGGElement>(null);
  const sections = useMemo(() => [...new Set(seatMap.seats.map((s) => s.section))], [seatMap.seats]);
  const layout = useMemo(() => overviewLayout(seatMap.seats, sections), [seatMap.seats, sections]);
  const [opened, setOpened] = useState<string | null>(null);
  const section = opened ?? (sections.length === 1 ? sections[0] : null);
  const visible = useMemo(() => seatMap.seats.filter((s) => s.section === section), [seatMap.seats, section]);
  const [focused, setFocused] = useState<number | null>(null);
  const { zoomIn, zoomOut, wasDrag } = usePanZoom(svg, group, section);
  const [announcement, setAnnouncement] = useState("");
  // Where keyboard focus goes once the next render has swapped overview and seats: a seat, or the section's toggle.
  const pendingFocus = useRef<{ seat: true } | { section: string } | null>(null);

  useEffect(() => {
    const target = pendingFocus.current;
    pendingFocus.current = null;
    if (!target) return;
    const found = "seat" in target ? svg.current?.querySelector<SVGElement>("[data-i][tabindex='0']") : [...(svg.current?.querySelectorAll<SVGElement>("[data-section]") ?? [])].find((g) => g.getAttribute("data-section") === target.section);
    found?.focus();
  }, [opened]);

  const tierById = useMemo(() => new Map(tiers.map((tier) => [tier.id, tier])), [tiers]);
  const box = section && visible.length > 0 ? boundsOf(visible) : null;
  const viewBox = box ? toViewBox({ x: box.x - PAD, y: box.y - PAD, width: box.width + PAD * 2, height: box.height + PAD * 2 }) : toViewBox(layout.viewBox);
  // One shape for the overview and every section, so opening a section never resizes the map.
  const aspect = Math.min(Math.max(layout.viewBox.width / layout.viewBox.height, MIN_ASPECT), MAX_ASPECT);
  const tabbable = focused !== null && visible.some((s) => s.index === focused) ? focused : visible[0]?.index;

  const indexOf = (target: EventTarget) => {
    const value = (target as Element).closest?.("[data-i]")?.getAttribute("data-i");
    return value === null || value === undefined ? null : Number(value);
  };

  const open = (name: string) => {
    const own = seatMap.seats.filter((s) => s.section === name);
    const free = own.filter((s) => store.get(s.index) === "free").length;
    setFocused((own.find((s) => store.get(s.index) !== "taken") ?? own[0])?.index ?? null);
    setAnnouncement(t("seats.sectionOpened", "vi", { name, count: free }));
    pendingFocus.current = { seat: true };
    setOpened(name);
  };

  const close = () => {
    if (!opened) return;
    setAnnouncement(t("seats.overviewOpened", "vi", { count: sections.length }));
    pendingFocus.current = { section: opened };
    setOpened(null);
  };

  const choose = (index: number) => {
    if (store.get(index) !== "taken") onToggle(index);
  };

  const click = (e: MouseEvent<SVGSVGElement>) => {
    if (wasDrag()) return;
    const index = indexOf(e.target);
    if (index !== null) {
      setFocused(index);
      choose(index);
      return;
    }

    const name = (e.target as Element).closest?.("[data-section]")?.getAttribute("data-section");
    if (name) open(name);
  };

  const keyDown = (e: KeyboardEvent<SVGSVGElement>) => {
    if (e.key === "Escape" && opened) {
      close();
      return;
    }

    const name = (e.target as Element).getAttribute?.("data-section");
    if (name && (e.key === "Enter" || e.key === " ")) {
      e.preventDefault();
      open(name);
      return;
    }

    const index = indexOf(e.target);
    if (index === null) return;
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      choose(index);
    } else if (SEAT_KEYS.has(e.key)) {
      e.preventDefault();
      const next = nextSeat(visible, index, e.key as SeatKey);
      if (next !== null) {
        setFocused(next);
        svg.current?.querySelector<SVGElement>(`[data-i="${next}"]`)?.focus();
      }
    }
  };

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center justify-between gap-2">
        {opened ? (
          <Button variant="outline" size="sm" onClick={close}>
            {t("seats.overview")}
          </Button>
        ) : (
          <span />
        )}
        {section ? (
          <div className="flex gap-1">
            <Button variant="outline" size="icon" aria-label={t("seats.zoomOut")} onClick={zoomOut}>
              <Minus aria-hidden />
            </Button>
            <Button variant="outline" size="icon" aria-label={t("seats.zoomIn")} onClick={zoomIn}>
              <Plus aria-hidden />
            </Button>
          </div>
        ) : null}
      </div>
      <svg
        ref={svg}
        viewBox={viewBox}
        role="group"
        aria-label={t("seats.mapLabel", "vi", { count: seatMap.seats.length })}
        style={{ aspectRatio: aspect }}
        className="w-full touch-none select-none rounded-xl border bg-muted/40"
        onClick={click}
        onKeyDown={keyDown}
      >
        <g ref={group}>
          {section ? (
            visible.map((seat) => {
              const tier = tierById.get(seat.tierId);
              return (
                <Seat
                  key={seat.index}
                  store={store}
                  seat={seat}
                  price={tier?.price ?? 0}
                  color={tier?.color ?? "#94a3b8"}
                  dimmed={tierFilter.size > 0 && !tierFilter.has(seat.tierId)}
                  tabbable={seat.index === tabbable}
                />
              );
            })
          ) : (
            <Overview seats={seatMap.seats} layout={layout} store={store} />
          )}
        </g>
      </svg>
      <p role="status" className="sr-only">
        {announcement}
      </p>
    </div>
  );
}

/** Free seats of a section; `revision` makes the React Compiler recount after every store change. */
function freeIn(seats: SeatInfo[], store: SeatStore, revision: number): number {
  void revision;
  return seats.filter((s) => store.get(s.index) === "free").length;
}

function Overview({ seats, layout, store }: { seats: SeatInfo[]; layout: OverviewLayout; store: SeatStore }) {
  // Free counts change with every delta; only the overview pays for recounting.
  const revision = useStoreRevision(store);
  // Type is sized to the venue (SVG units), so it reads the same on a club floor plan and a stadium.
  const font = layout.viewBox.width / 32;
  const { stage } = layout;
  return (
    <>
      <rect x={stage.x} y={stage.y} width={stage.width} height={stage.height} rx={stage.height / 4} className="fill-foreground/80" />
      <text x={stage.x + stage.width / 2} y={stage.y + stage.height / 2} dominantBaseline="central" textAnchor="middle" fontSize={Math.min(font, stage.height * 0.6)} className="fill-background font-semibold">
        {t("seats.stage")}
      </text>
      {layout.sections.map(({ name, box }) => {
        const own = seats.filter((s) => s.section === name);
        const free = freeIn(own, store, revision);
        const cx = box.x + box.width / 2;
        const cy = box.y + box.height / 2;
        return (
          <g key={name} data-section={name} role="button" tabIndex={0} aria-label={t("seats.sectionLabel", "vi", { name, count: free })} className="cursor-pointer outline-none focus-visible:[&>rect]:stroke-ring">
            <rect
              x={box.x}
              y={box.y}
              width={box.width}
              height={box.height}
              rx={Math.min(box.width, box.height) / 10}
              className={free > 0 ? "fill-primary/15 stroke-primary/40" : "fill-muted stroke-border"}
              strokeWidth={2}
              vectorEffect="non-scaling-stroke"
            />
            <text x={cx} y={cy - font * 0.35} textAnchor="middle" fontSize={font} className="fill-foreground font-semibold">
              {name}
            </text>
            <text aria-hidden x={cx} y={cy + font * 0.85} textAnchor="middle" fontSize={font * 0.6} className="fill-muted-foreground">
              {free > 0 ? t("seats.sectionFree", "vi", { count: free, total: own.length }) : t("seats.sectionFull")}
            </text>
          </g>
        );
      })}
    </>
  );
}
