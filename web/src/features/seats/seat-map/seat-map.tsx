"use client";

import { useMemo, useRef, useState, type KeyboardEvent, type MouseEvent } from "react";
import { Minus, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { TicketTier } from "@/lib/catalog/types";
import { t } from "@/lib/i18n";
import type { SeatStore } from "@/lib/seats/seat-store";
import type { SeatInfo, SeatMap as SeatMapData } from "@/lib/seats/types";
import { useStoreRevision } from "@/lib/seats/use-seat";
import { nextSeat, SEAT_KEYS, type SeatKey } from "./keyboard";
import { Seat, SEAT_RADIUS } from "./seat";
import { usePanZoom } from "./use-pan-zoom";

const CANVAS = { width: 1000, height: 700 };
const PAD = 30;

type SeatMapProps = {
  seatMap: SeatMapData;
  tiers: TicketTier[];
  store: SeatStore;
  /** Tier ids to highlight; empty = all. Other seats are dimmed, never hidden. */
  tierFilter: Set<string>;
  onToggle: (index: number) => void;
};

type Box = { x: number; y: number; width: number; height: number };

function boundsOf(seats: SeatInfo[]): Box {
  const xs = seats.map((s) => s.x);
  const ys = seats.map((s) => s.y);
  const x = Math.min(...xs) - SEAT_RADIUS;
  const y = Math.min(...ys) - SEAT_RADIUS;
  return { x, y, width: Math.max(...xs) + SEAT_RADIUS - x, height: Math.max(...ys) + SEAT_RADIUS - y };
}

/**
 * Seat map of spec 07 (dynamically imported by the page). Overview of sections first; opening a section shows its seats
 * with wheel/pinch zoom and drag. One delegated click/keydown handler on the SVG; seats are memoized and subscribe to
 * their own index (spike S1: p95 16.7 ms per frame with 10,000 seats).
 */
export function SeatMap({ seatMap, tiers, store, tierFilter, onToggle }: SeatMapProps) {
  const svg = useRef<SVGSVGElement>(null);
  const group = useRef<SVGGElement>(null);
  const sections = useMemo(() => [...new Set(seatMap.seats.map((s) => s.section))], [seatMap.seats]);
  const [opened, setOpened] = useState<string | null>(null);
  const section = opened ?? (sections.length === 1 ? sections[0] : null);
  const visible = useMemo(() => seatMap.seats.filter((s) => s.section === section), [seatMap.seats, section]);
  const [focused, setFocused] = useState<number | null>(null);
  const { zoomIn, zoomOut, wasDrag } = usePanZoom(svg, group, section);

  const tierById = useMemo(() => new Map(tiers.map((tier) => [tier.id, tier])), [tiers]);
  const box = section && visible.length > 0 ? boundsOf(visible) : { x: 0, y: 0, ...CANVAS };
  const viewBox = section ? `${box.x - PAD} ${box.y - PAD} ${box.width + PAD * 2} ${box.height + PAD * 2}` : `0 0 ${CANVAS.width} ${CANVAS.height}`;
  const tabbable = focused !== null && visible.some((s) => s.index === focused) ? focused : visible[0]?.index;

  const indexOf = (target: EventTarget) => {
    const value = (target as Element).closest?.("[data-i]")?.getAttribute("data-i");
    return value === null || value === undefined ? null : Number(value);
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
    if (name) setOpened(name);
  };

  const keyDown = (e: KeyboardEvent<SVGSVGElement>) => {
    if (e.key === "Escape" && opened) {
      setOpened(null);
      return;
    }

    const name = (e.target as Element).getAttribute?.("data-section");
    if (name && (e.key === "Enter" || e.key === " ")) {
      e.preventDefault();
      setOpened(name);
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
          <Button variant="outline" size="sm" onClick={() => setOpened(null)}>
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
        className="aspect-[10/7] w-full touch-none select-none rounded-xl border bg-muted/40"
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
            <Overview seats={seatMap.seats} sections={sections} store={store} />
          )}
        </g>
      </svg>
    </div>
  );
}

function Overview({ seats, sections, store }: { seats: SeatInfo[]; sections: string[]; store: SeatStore }) {
  // Free counts change with every delta; only the overview pays for recounting.
  useStoreRevision(store);
  return (
    <>
      <rect x={350} y={20} width={300} height={44} rx={8} className="fill-foreground/80" />
      <text x={500} y={48} textAnchor="middle" className="fill-background text-[18px] font-semibold">
        {t("seats.stage")}
      </text>
      {sections.map((name) => {
        const own = seats.filter((s) => s.section === name);
        const box = boundsOf(own);
        const free = own.filter((s) => store.get(s.index) === "free").length;
        return (
          <g key={name} data-section={name} role="button" tabIndex={0} aria-label={t("seats.sectionLabel", "vi", { name, count: free })} className="cursor-pointer outline-none focus-visible:[&>rect]:stroke-ring">
            <rect x={box.x - 6} y={box.y - 6} width={box.width + 12} height={box.height + 12} rx={10} className="fill-primary/15 stroke-primary/40" strokeWidth={2} />
            <text x={box.x + box.width / 2} y={box.y + box.height / 2 + 6} textAnchor="middle" className="fill-foreground text-[20px] font-semibold">
              {name}
            </text>
          </g>
        );
      })}
    </>
  );
}
