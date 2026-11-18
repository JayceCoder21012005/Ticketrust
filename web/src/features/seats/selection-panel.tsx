"use client";

import { Minus, Plus, Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { TicketTier } from "@/lib/catalog/types";
import { formatVnd } from "@/lib/format";
import { t } from "@/lib/i18n.buyer";
import { seatPlace } from "@/lib/seats/format";
import type { SeatStore } from "@/lib/seats/seat-store";
import type { SeatMap } from "@/lib/seats/types";
import { useStoreRevision } from "@/lib/seats/use-seat";

/** Heading of the summary panel (selection or hold); focus lands here when the panel swaps. */
export const PANEL_TITLE_SELECTOR = "[data-panel-title]";

type SelectionPanelProps = {
  map: SeatMap;
  store: SeatStore;
  tiers: TicketTier[];
  limit: number;
  ga: Record<string, number>;
  gaRemaining: Record<string, number>;
  busy: boolean;
  onGaChange: (tierId: string, quantity: number) => void;
  onBestAvailable: (tierId: string, quantity: number) => void;
  onContinue: () => void;
};

/**
 * Seats the buyer picked. `revision` is an explicit input on purpose: the React Compiler memoizes calls by their
 * arguments, and `store` alone never changes identity, so without it the panel would keep showing a stale selection.
 */
function selectedSeats(map: SeatMap, store: SeatStore, revision: number) {
  void revision;
  return store.selected().map((i) => map.seats[i]).filter(Boolean);
}

/** What the buyer picked, GA steppers, best available per seated tier, and "Tiếp tục" (spec 07). */
export function SelectionPanel({ map, store, tiers, limit, ga, gaRemaining, busy, onGaChange, onBestAvailable, onContinue }: SelectionPanelProps) {
  const revision = useStoreRevision(store);
  const price = new Map(tiers.map((tier) => [tier.id, tier.price]));
  const selected = selectedSeats(map, store, revision);
  const gaCount = Object.values(ga).reduce((sum, q) => sum + q, 0);
  const count = selected.length + gaCount;
  const total = selected.reduce((sum, s) => sum + (price.get(s.tierId) ?? 0), 0) + tiers.reduce((sum, tier) => sum + (ga[tier.id] ?? 0) * tier.price, 0);
  const seated = tiers.filter((tier) => tier.kind === "Seated" && map.seats.some((s) => s.tierId === tier.id));
  const standing = tiers.filter((tier) => tier.kind === "GeneralAdmission");
  const bestCount = Math.min(2, limit);

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-2">
        <h2 data-panel-title="" tabIndex={-1} className="text-base font-semibold outline-none">
          {t("seats.summary.title")}
        </h2>
        <p className="text-xs text-muted-foreground">{t("seats.limit", "vi", { count: limit })}</p>
        {selected.length === 0 && gaCount === 0 ? (
          <p className="text-sm text-muted-foreground">{t("seats.summary.empty")}</p>
        ) : (
          <ul className="flex flex-col gap-1 text-sm">
            {selected.map((s) => (
              <li key={s.index} className="flex justify-between gap-2">
                <span>{seatPlace(s)}</span>
                <span className="tabular-nums">{formatVnd(price.get(s.tierId) ?? 0)}</span>
              </li>
            ))}
          </ul>
        )}
      </div>

      {seated.length > 0 ? (
        <div className="flex flex-col gap-2">
          {seated.map((tier) => (
            <Button key={tier.id} variant="outline" size="sm" disabled={busy} onClick={() => onBestAvailable(tier.id, bestCount)}>
              <Sparkles aria-hidden />
              {`${t("seats.bestAvailable")} · ${bestCount} ${tier.name}`}
            </Button>
          ))}
        </div>
      ) : null}

      {standing.length > 0 ? (
        <div className="flex flex-col gap-2">
          <h3 className="text-sm font-medium">{t("seats.ga.title")}</h3>
          {standing.map((tier) => {
            const quantity = ga[tier.id] ?? 0;
            const max = Math.min(quantity + (limit - count), gaRemaining[tier.id] ?? 0);
            return (
              <div key={tier.id} className="flex items-center justify-between gap-2 text-sm">
                <div className="flex flex-col">
                  <span>{`${tier.name} · ${formatVnd(tier.price)}`}</span>
                  <span className="text-xs text-muted-foreground">{t("seats.ga.remaining", "vi", { count: gaRemaining[tier.id] ?? 0 })}</span>
                </div>
                <div className="flex items-center gap-2">
                  <Button variant="outline" size="icon" aria-label={t("seats.ga.decrease", "vi", { name: tier.name })} disabled={quantity === 0} onClick={() => onGaChange(tier.id, quantity - 1)}>
                    <Minus aria-hidden />
                  </Button>
                  <span data-testid={`ga-quantity-${tier.id}`} className="w-6 text-center tabular-nums" aria-live="polite">
                    {quantity}
                  </span>
                  <Button variant="outline" size="icon" aria-label={t("seats.ga.increase", "vi", { name: tier.name })} disabled={quantity >= max} onClick={() => onGaChange(tier.id, quantity + 1)}>
                    <Plus aria-hidden />
                  </Button>
                </div>
              </div>
            );
          })}
        </div>
      ) : null}

      <p className="flex justify-between border-t pt-2 font-semibold" aria-live="polite" aria-atomic="true">
        <span>{t("seats.summary.total")}</span>
        <span className="tabular-nums">{formatVnd(total)}</span>
      </p>
      <Button disabled={busy || count === 0 || count > limit} onClick={onContinue}>
        {busy ? t("seats.summary.holding") : t("seats.summary.continue")}
      </Button>
    </div>
  );
}

/** Tier chips filter the map (other seats dim); the state legend never relies on colour alone. */
export function SeatLegend({ tiers, filter, onToggle }: { tiers: TicketTier[]; filter: Set<string>; onToggle: (tierId: string) => void }) {
  return (
    <div className="flex flex-col gap-2">
      <div role="group" aria-label={t("seats.legend.filter")} className="flex flex-wrap gap-2">
        {tiers
          .filter((tier) => tier.kind === "Seated")
          .map((tier) => (
            <Button key={tier.id} variant={filter.has(tier.id) ? "default" : "outline"} size="sm" aria-pressed={filter.has(tier.id)} onClick={() => onToggle(tier.id)}>
              <span aria-hidden className="size-3 rounded-full" style={{ backgroundColor: tier.color }} />
              {`${tier.name} · ${formatVnd(tier.price)}`}
            </Button>
          ))}
      </div>
      <ul className="flex flex-wrap gap-4 text-xs text-muted-foreground">
        <li className="flex items-center gap-1">
          <span aria-hidden className="size-3 rounded-full bg-primary" />
          {t("seats.legend.free")}
        </li>
        <li className="flex items-center gap-1">
          <span aria-hidden className="size-3 rounded-full border-2 border-foreground bg-primary" />
          {t("seats.legend.selected")}
        </li>
        <li className="flex items-center gap-1">
          <span aria-hidden className="size-3 rounded-full border-2 border-foreground bg-foreground" />
          {t("seats.legend.mine")}
        </li>
        <li className="flex items-center gap-1">
          <span aria-hidden className="size-2 rounded-full bg-muted-foreground/40" />
          {t("seats.legend.taken")}
        </li>
      </ul>
    </div>
  );
}
