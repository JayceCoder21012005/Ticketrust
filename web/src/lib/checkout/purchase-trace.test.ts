import { afterEach, describe, expect, it } from "vitest";
import { beginPurchaseTrace, currentPurchaseTrace, newTraceParent, resetPurchaseTrace } from "./purchase-trace";

const W3C = /^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/;

afterEach(() => resetPurchaseTrace());

describe("newTraceParent", () => {
  it("is a sampled W3C traceparent with random, non-zero ids", () => {
    const a = newTraceParent();
    expect(a).toMatch(W3C);
    expect(a).not.toBe(newTraceParent());
    expect(newTraceParent(() => new Uint8Array(24))).toMatch(/^00-0{31}1-0{15}1-01$/);
  });
});

describe("purchase trace", () => {
  it("keeps one trace for the hold, the order and the payment, and starts another for the next purchase", () => {
    expect(currentPurchaseTrace()).toBeUndefined();

    const first = beginPurchaseTrace("hold-key-1");
    expect(beginPurchaseTrace("hold-key-1")).toBe(first); // a retried hold is the same purchase
    expect(currentPurchaseTrace()).toBe(first);

    const second = beginPurchaseTrace("hold-key-2");
    expect(second).not.toBe(first);
    expect(currentPurchaseTrace()).toBe(second);
  });

  it("survives a reload in the same tab", () => {
    const trace = beginPurchaseTrace("k");
    resetPurchaseTrace({ keepStorage: true });
    expect(currentPurchaseTrace()).toBe(trace);
  });
});
