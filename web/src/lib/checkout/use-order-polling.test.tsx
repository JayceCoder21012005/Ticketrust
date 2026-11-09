import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";
import type { Order, OrderStatus } from "./types";
import { useOrderPolling } from "./use-order-polling";

const orderOf = (status: OrderStatus): Order => ({
  id: "o1",
  eventId: "e1",
  status,
  seats: [],
  ga: [],
  total: 0,
  buyer: { name: "An", email: "an@example.com", phone: null },
  expiresAt: "2026-12-01T10:10:00Z",
  serverNow: "2026-12-01T10:00:00Z",
  createdAt: "2026-12-01T10:00:00Z",
  paidAt: null,
  refundReason: null,
  ticketCount: null,
});

const flush = (ms: number) => act(() => vi.advanceTimersByTimeAsync(ms));

function setHidden(hidden: boolean) {
  Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
  document.dispatchEvent(new Event("visibilitychange"));
}

describe("useOrderPolling", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    setHidden(false);
    vi.useRealTimers();
  });

  it("polls every 1.5 s while the order is unsettled and stops on a terminal state", async () => {
    const fetchOrder = vi.fn().mockResolvedValueOnce(orderOf("AwaitingPayment")).mockResolvedValueOnce(orderOf("AwaitingPayment")).mockResolvedValue(orderOf("Paid"));
    const { result } = renderHook(() => useOrderPolling("o1", { fetchOrder }));

    await flush(0);
    expect(fetchOrder).toHaveBeenCalledTimes(1);
    expect(result.current.phase).toBe("polling");
    await flush(1_500);
    expect(fetchOrder).toHaveBeenCalledTimes(2);
    await flush(1_500);
    expect(fetchOrder).toHaveBeenCalledTimes(3);
    expect(result.current.phase).toBe("settled");
    expect(result.current.order?.status).toBe("Paid");

    await flush(10_000);
    expect(fetchOrder).toHaveBeenCalledTimes(3);
  });

  it.each(["PaymentFailed", "Cancelled", "Expired", "RefundPending", "Refunded"] as const)("stops on %s", async (status) => {
    const fetchOrder = vi.fn().mockResolvedValue(orderOf(status));
    const { result } = renderHook(() => useOrderPolling("o1", { fetchOrder }));

    await flush(5_000);

    expect(fetchOrder).toHaveBeenCalledTimes(1);
    expect(result.current.phase).toBe("settled");
  });

  it("gives up after 90 s and a manual refresh starts a new window", async () => {
    const fetchOrder = vi.fn().mockResolvedValue(orderOf("AwaitingPayment"));
    const { result } = renderHook(() => useOrderPolling("o1", { fetchOrder }));

    await flush(89_000);
    expect(result.current.phase).toBe("polling");
    await flush(2_000);
    expect(result.current.phase).toBe("timeout");
    const calls = fetchOrder.mock.calls.length;
    expect(calls).toBeLessThanOrEqual(61);
    await flush(10_000);
    expect(fetchOrder).toHaveBeenCalledTimes(calls);

    fetchOrder.mockResolvedValue(orderOf("Paid"));
    act(() => result.current.refresh());
    await flush(0);
    expect(result.current.phase).toBe("settled");
  });

  it("does not poll or count time while the tab is hidden", async () => {
    const fetchOrder = vi.fn().mockResolvedValue(orderOf("AwaitingPayment"));
    const { result } = renderHook(() => useOrderPolling("o1", { fetchOrder }));
    await flush(0);
    setHidden(true);
    const calls = fetchOrder.mock.calls.length;

    await flush(120_000);
    expect(fetchOrder).toHaveBeenCalledTimes(calls);
    expect(result.current.phase).toBe("polling");

    setHidden(false);
    await flush(0);
    expect(fetchOrder.mock.calls.length).toBeGreaterThan(calls);
  });

  it("runs a single chain when the tab goes hidden and visible during a fetch", async () => {
    let finish: (order: Order) => void = () => undefined;
    const fetchOrder = vi
      .fn()
      .mockImplementationOnce(() => new Promise<Order>((resolve) => (finish = resolve)))
      .mockResolvedValue(orderOf("AwaitingPayment"));
    renderHook(() => useOrderPolling("o1", { fetchOrder }));
    await flush(0);

    setHidden(true);
    setHidden(false);
    await flush(0);
    expect(fetchOrder).toHaveBeenCalledTimes(1);

    finish(orderOf("AwaitingPayment"));
    await flush(6_000);
    expect(fetchOrder.mock.calls.length).toBeLessThanOrEqual(6);
  });

  it("keeps polling through a transient failure but stops on 404", async () => {
    const fetchOrder = vi.fn().mockRejectedValueOnce(new TypeError("offline")).mockResolvedValue(orderOf("Paid"));
    const { result } = renderHook(() => useOrderPolling("o1", { fetchOrder }));
    await flush(0);
    expect(result.current.phase).toBe("polling");
    await flush(1_500);
    expect(result.current.phase).toBe("settled");

    const missing = vi.fn().mockRejectedValue(new ApiError(404, { code: "order.not_found" }));
    const second = renderHook(() => useOrderPolling("o2", { fetchOrder: missing }));
    await flush(0);
    expect(second.result.current.phase).toBe("error");
    expect(missing).toHaveBeenCalledTimes(1);
  });
});
