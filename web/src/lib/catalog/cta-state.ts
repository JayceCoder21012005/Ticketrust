import type { EventStatus, SalePhase, TierRemaining } from "./types";

/** "Vào phòng chờ" appears this long before a queued sale opens (spec 04). */
export const PRE_QUEUE_WINDOW_MS = 30 * 60_000;

export type CtaKind = "Upcoming" | "PreQueue" | "OnSale" | "SoldOut" | "Ended" | "Cancelled" | "SalesClosed" | "NotScheduled";

export type CtaInput = { status: EventStatus; endsAt: string; isSoldOut: boolean; phases: SalePhase[] };

export type CtaState = {
  kind: CtaKind;
  /** The phase the CTA is about: the open one, or the next one to open. */
  phase?: SalePhase;
  opensAt?: string;
  /** Server-clock instant (ms) when the state changes next; the CTA re-evaluates then, without polling. */
  nextChangeAt?: number;
};

/**
 * Pure CTA state machine of spec 04. `nowMs` must be server time (device time + clock offset, C10), so a
 * device clock that is minutes off still flips the button at the right moment.
 */
export function computeCtaState(event: CtaInput, nowMs: number, tiers?: TierRemaining[]): CtaState {
  if (event.status === "Cancelled") {
    return { kind: "Cancelled" };
  }

  if (event.status === "Ended" || nowMs >= Date.parse(event.endsAt)) {
    return { kind: "Ended" };
  }

  // Taken down by an admin (spec 16): no sales until it is restored.
  if (event.status === "Suspended") {
    return { kind: "SalesClosed" };
  }

  if (event.isSoldOut || (tiers !== undefined && tiers.length > 0 && tiers.every((tier) => tier.remaining <= 0))) {
    return { kind: "SoldOut" };
  }

  const phases = [...event.phases].sort((a, b) => Date.parse(a.opensAt) - Date.parse(b.opensAt));
  const open = phases.find((p) => Date.parse(p.opensAt) <= nowMs && nowMs < Date.parse(p.closesAt));
  if (open) {
    return { kind: "OnSale", phase: open, nextChangeAt: Date.parse(open.closesAt) };
  }

  const next = phases.find((p) => Date.parse(p.opensAt) > nowMs);
  if (!next) {
    return { kind: phases.length > 0 ? "SalesClosed" : "NotScheduled" };
  }

  const opens = Date.parse(next.opensAt);
  if (next.queueEnabled && opens - nowMs <= PRE_QUEUE_WINDOW_MS) {
    return { kind: "PreQueue", phase: next, opensAt: next.opensAt, nextChangeAt: opens };
  }

  return {
    kind: "Upcoming",
    phase: next,
    opensAt: next.opensAt,
    nextChangeAt: next.queueEnabled ? opens - PRE_QUEUE_WINDOW_MS : opens,
  };
}
