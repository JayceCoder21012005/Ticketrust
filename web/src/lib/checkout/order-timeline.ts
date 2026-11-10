import { refundReasonKey } from "./order-state";
import type { Order, OrderStatus } from "./types";

export type StepKey = "created" | "awaiting" | "paid" | "failed" | "cancelled" | "expired" | "refunding" | "refunded";

/** `done` happened, `current` is where the order is now, `upcoming` is still ahead. */
export type StepState = "done" | "current" | "upcoming";

export type TimelineStep = {
  key: StepKey;
  state: StepState;
  /** The api's own timestamp for this step, or null when it does not record one (nothing is invented). */
  at: string | null;
  /** The step went wrong (failed, cancelled, expired): shown with a different icon, never by colour alone. */
  problem: boolean;
  /** i18n keys of the sentences that explain the step (the refund's reason), if any. */
  notes: string[];
};

type Facts = Pick<Order, "status" | "createdAt" | "paidAt" | "expiresAt" | "refundReason">;

/**
 * Where each status sits on the path. The last entry of a settled path is the current step; the steps before it are
 * done. An open order lists the steps still ahead, a refund path lists the payment it reverses.
 */
const PATH: Record<OrderStatus, { steps: StepKey[]; current: StepKey }> = {
  Pending: { steps: ["created", "awaiting", "paid"], current: "created" },
  AwaitingPayment: { steps: ["created", "awaiting", "paid"], current: "awaiting" },
  PaymentFailed: { steps: ["created", "awaiting", "failed", "paid"], current: "failed" },
  Paid: { steps: ["created", "awaiting", "paid"], current: "paid" },
  Cancelled: { steps: ["created", "cancelled"], current: "cancelled" },
  Expired: { steps: ["created", "expired"], current: "expired" },
  RefundPending: { steps: ["created", "awaiting", "paid", "refunding", "refunded"], current: "refunding" },
  Refunded: { steps: ["created", "awaiting", "paid", "refunding", "refunded"], current: "refunded" },
};

const PROBLEMS = new Set<StepKey>(["failed", "cancelled", "expired"]);

function timeOf(key: StepKey, facts: Facts): string | null {
  switch (key) {
    case "created":
      return facts.createdAt;
    case "paid":
      return facts.paidAt;
    case "expired":
      // The order's own deadline is when it stopped being payable.
      return facts.expiresAt;
    default:
      return null;
  }
}

const CURRENT_NOTES = new Set<StepKey>(["created", "awaiting", "failed", "cancelled", "expired", "refunded"]);

/** A sentence for the step the order is on; a refund also keeps its reason once it is done. */
function notesOf(key: StepKey, current: boolean, facts: Facts): string[] {
  if (key === "refunding") {
    return current ? [refundReasonKey(facts.refundReason), "checkout.result.refunding.tail"] : [refundReasonKey(facts.refundReason)];
  }

  return current && CURRENT_NOTES.has(key) ? [`orders.timeline.notes.${key}`] : [];
}

/** The status timeline of one order, derived from its status and the timestamps the api returns. */
export function buildTimeline(facts: Facts): TimelineStep[] {
  const { steps, current } = PATH[facts.status];
  const at = steps.indexOf(current);
  return steps.map((key, index) => ({
    key,
    state: index < at ? "done" : index === at ? "current" : "upcoming",
    at: timeOf(key, facts),
    problem: PROBLEMS.has(key),
    notes: notesOf(key, index === at, facts),
  }));
}

export const statusLabelKey = (status: OrderStatus): string => `orders.status.${status}`;

export type StatusTone = "success" | "warning" | "danger" | "info" | "neutral";

const TONE: Record<OrderStatus, StatusTone> = {
  Pending: "warning",
  AwaitingPayment: "warning",
  PaymentFailed: "danger",
  Paid: "success",
  Cancelled: "neutral",
  Expired: "neutral",
  RefundPending: "info",
  Refunded: "info",
};

export const statusToneOf = (status: OrderStatus): StatusTone => TONE[status];
