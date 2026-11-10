import { describe, expect, it } from "vitest";
import { buildTimeline, statusLabelKey } from "./order-timeline";
import type { Order, OrderStatus } from "./types";

const CREATED = "2026-12-01T10:00:00Z";
const PAID = "2026-12-01T10:03:00Z";
const EXPIRES = "2026-12-01T10:10:00Z";

const order = (status: OrderStatus, extra: Partial<Order> = {}): Pick<Order, "status" | "createdAt" | "paidAt" | "expiresAt" | "refundReason"> => ({
  status,
  createdAt: CREATED,
  paidAt: null,
  expiresAt: EXPIRES,
  refundReason: null,
  ...extra,
});

const shape = (o: ReturnType<typeof order>) => buildTimeline(o).map((s) => `${s.key}:${s.state}`);

describe("buildTimeline", () => {
  it.each<[OrderStatus, string[]]>([
    ["Pending", ["created:current", "awaiting:upcoming", "paid:upcoming"]],
    ["AwaitingPayment", ["created:done", "awaiting:current", "paid:upcoming"]],
    ["PaymentFailed", ["created:done", "awaiting:done", "failed:current", "paid:upcoming"]],
    ["Paid", ["created:done", "awaiting:done", "paid:current"]],
    ["Cancelled", ["created:done", "cancelled:current"]],
    ["Expired", ["created:done", "expired:current"]],
    ["RefundPending", ["created:done", "awaiting:done", "paid:done", "refunding:current", "refunded:upcoming"]],
    ["Refunded", ["created:done", "awaiting:done", "paid:done", "refunding:done", "refunded:current"]],
  ])("%s", (status, expected) => {
    expect(shape(order(status))).toEqual(expected);
  });

  it("has exactly one current step for every status", () => {
    const statuses: OrderStatus[] = ["Pending", "AwaitingPayment", "PaymentFailed", "Paid", "Cancelled", "Expired", "RefundPending", "Refunded"];
    for (const status of statuses) {
      expect(buildTimeline(order(status)).filter((s) => s.state === "current")).toHaveLength(1);
    }
  });

  it("stamps only the times the api gives: created, paid and (for an expired order) its deadline", () => {
    const paid = buildTimeline(order("Paid", { paidAt: PAID }));
    expect(paid.map((s) => s.at)).toEqual([CREATED, null, PAID]);

    expect(buildTimeline(order("Expired")).map((s) => s.at)).toEqual([CREATED, EXPIRES]);
    expect(buildTimeline(order("Refunded", { paidAt: PAID })).map((s) => s.at)).toEqual([CREATED, null, PAID, null, null]);
    expect(buildTimeline(order("Cancelled")).map((s) => s.at)).toEqual([CREATED, null]);
  });

  it("flags the steps that went wrong", () => {
    const problems = (status: OrderStatus) => buildTimeline(order(status)).filter((s) => s.problem).map((s) => s.key);
    expect(problems("PaymentFailed")).toEqual(["failed"]);
    expect(problems("Cancelled")).toEqual(["cancelled"]);
    expect(problems("Expired")).toEqual(["expired"]);
    expect(problems("Paid")).toEqual([]);
    expect(problems("RefundPending")).toEqual([]);
  });

  it("explains a refund with the api's reason, and an unknown one with the general sentence", () => {
    const refunding = (reason: string | null) => buildTimeline(order("RefundPending", { refundReason: reason })).find((s) => s.key === "refunding");
    expect(refunding("event_cancelled")?.notes[0]).toBe("checkout.result.refunding.reasons.event_cancelled");
    expect(refunding("seats_unavailable")?.notes[0]).toBe("checkout.result.refunding.reasons.seats_unavailable");
    expect(refunding("something_new")?.notes[0]).toBe("checkout.result.refunding.reasons.other");
    expect(refunding(null)?.notes[0]).toBe("checkout.result.refunding.reasons.other");
    // Honest: no number of days, only that the bank decides when the money arrives.
    expect(refunding("event_cancelled")?.notes).toContain("checkout.result.refunding.tail");
  });

  it("keeps the refund reason visible once the refund is done, without the 'still to come' sentence", () => {
    const done = buildTimeline(order("Refunded", { refundReason: "event_ended" })).find((s) => s.key === "refunding");
    expect(done?.notes).toEqual(["checkout.result.refunding.reasons.event_ended"]);
  });

  it("adds a sentence to the step the order is on, never to the steps behind or ahead", () => {
    const notes = (status: OrderStatus) => buildTimeline(order(status)).map((s) => s.notes.length);
    expect(notes("Pending")).toEqual([1, 0, 0]);
    expect(notes("AwaitingPayment")).toEqual([0, 1, 0]);
    expect(notes("PaymentFailed")).toEqual([0, 0, 1, 0]);
    expect(notes("Cancelled")).toEqual([0, 1]);
    expect(notes("Expired")).toEqual([0, 1]);
    expect(notes("Paid")).toEqual([0, 0, 0]);
    expect(buildTimeline(order("Expired"))[1]?.notes).toEqual(["orders.timeline.notes.expired"]);
  });
});

describe("statusLabelKey", () => {
  it("has a label key for each status", () => {
    expect(statusLabelKey("RefundPending")).toBe("orders.status.RefundPending");
  });
});
