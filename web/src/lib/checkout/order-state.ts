import { clockOffsetMs, remainingMs } from "@/lib/clock";
import type { Order, OrderStatus } from "./types";

/** What the result page shows (spec 08): the eight order statuses fold into seven states. */
export type ResultKind = "confirming" | "success" | "failed" | "expired" | "refunding" | "refunded" | "cancelled";

const KIND: Record<OrderStatus, ResultKind> = {
  Pending: "confirming",
  AwaitingPayment: "confirming",
  PaymentFailed: "failed",
  Paid: "success",
  Cancelled: "cancelled",
  Expired: "expired",
  RefundPending: "refunding",
  Refunded: "refunded",
};

export const resultKindOf = (status: OrderStatus): ResultKind => KIND[status];

/** Polling stops here: nothing the buyer waits for can still change the outcome (a refund still lands later). */
export const isSettled = (status: OrderStatus): boolean => KIND[status] !== "confirming";

/** A buyer can pay (or retry paying) while the order is open. */
export const isPayable = (status: OrderStatus): boolean => status === "Pending" || status === "AwaitingPayment" || status === "PaymentFailed";

/** Only a `Pending` order can be cancelled: once a payment session exists the api answers 409. */
export const isCancellable = (status: OrderStatus): boolean => status === "Pending";

/** Whether the order's own deadline had passed when the api answered (judged on server time, C10). */
export function isPastDeadline(order: Pick<Order, "expiresAt" | "serverNow">): boolean {
  return remainingMs(order.expiresAt, 0, clockOffsetMs(order.serverNow, 0)) === 0;
}

const REFUND_REASONS = new Set([
  "event_cancelled",
  "event_ended",
  "seats_unavailable",
  "purchase_limit_exceeded",
  "order_closed",
  "amount_mismatch",
  "duplicate_payment",
]);

/** i18n key of the honest explanation for a refund; an unknown (future) reason falls back to the general one. */
export const refundReasonKey = (reason: string | null): string =>
  `checkout.result.refunding.reasons.${reason !== null && REFUND_REASONS.has(reason) ? reason : "other"}`;
