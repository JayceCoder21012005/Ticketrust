import { memo } from "react";
import { seatLabel } from "@/lib/seats/format";
import type { SeatStore } from "@/lib/seats/seat-store";
import type { SeatInfo } from "@/lib/seats/types";
import { useSeat } from "@/lib/seats/use-seat";

export const SEAT_RADIUS = 8;

type SeatProps = {
  store: SeatStore;
  seat: SeatInfo;
  price: number;
  color: string;
  dimmed: boolean;
  /** Roving tabindex: exactly one seat on screen is in the tab order. */
  tabbable: boolean;
};

/**
 * One seat. Subscribes to its own index only, so a delta re-renders just the seats it names. State is never shown by
 * colour alone: selected and held-by-you seats get a ring, taken seats shrink and turn grey.
 */
export const Seat = memo(function Seat({ store, seat, price, color, dimmed, tabbable }: SeatProps) {
  const state = useSeat(store, seat.index);
  const taken = state === "taken";
  const ringed = state === "selected" || state === "mine";

  return (
    <circle
      data-i={seat.index}
      data-state={state}
      data-dimmed={dimmed ? "true" : undefined}
      cx={seat.x}
      cy={seat.y}
      r={taken ? SEAT_RADIUS - 3 : SEAT_RADIUS}
      fill={taken ? "var(--muted-foreground)" : state === "mine" ? "var(--foreground)" : color}
      fillOpacity={taken ? 0.35 : 1}
      opacity={dimmed ? 0.25 : 1}
      stroke={ringed ? "var(--foreground)" : "none"}
      strokeWidth={ringed ? 3 : 0}
      role="button"
      tabIndex={tabbable ? 0 : -1}
      aria-label={seatLabel(seat, price, state)}
      aria-pressed={ringed}
      aria-disabled={taken || undefined}
      className="cursor-pointer outline-none focus-visible:stroke-ring focus-visible:[stroke-width:4]"
    />
  );
});
