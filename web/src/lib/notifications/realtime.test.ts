import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type Handler = (...args: unknown[]) => void;

const connection = {
  handlers: new Map<string, Handler>(),
  reconnected: undefined as (() => void) | undefined,
  closed: undefined as (() => void) | undefined,
  start: vi.fn(() => Promise.resolve()),
  // Like the real HubConnection, stopping a started connection runs the onclose handlers.
  stop: vi.fn(() => {
    connection.closed?.();
    return Promise.resolve();
  }),
  invoke: vi.fn(() => Promise.resolve()),
  on(name: string, handler: Handler) {
    this.handlers.set(name, handler);
  },
  onreconnected(handler: () => void) {
    this.reconnected = handler;
  },
  onclose(handler: () => void) {
    this.closed = handler;
  },
};

const withUrl = vi.fn();
const reconnectPolicy = vi.fn();
vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: class {
    withUrl(url: string, options: unknown) {
      withUrl(url, options);
      return this;
    }
    withAutomaticReconnect(policy: unknown) {
      reconnectPolicy(policy);
      return this;
    }
    build() {
      return connection;
    }
  },
}));

const { connectUserFeed } = await import("./realtime");

const options = (extra: Partial<Parameters<typeof connectUserFeed>[0]> = {}) => ({
  accessToken: () => Promise.resolve("tok"),
  onNotification: vi.fn(),
  onOrderStatus: vi.fn(),
  onResync: vi.fn(),
  ...extra,
});

const item = { id: "n1", type: "OrderPaid", title: "Thanh toán thành công", body: "Đơn #1", link: "/account/orders/o1", read: false, createdAt: "2026-12-01T10:00:00Z" };

describe("connectUserFeed", () => {
  it("connects to the user hub without joining anything, then asks for a resync", async () => {
    const onResync = vi.fn();
    connectUserFeed(options({ onResync }));

    await vi.waitFor(() => expect(onResync).toHaveBeenCalledTimes(1));
    expect(withUrl).toHaveBeenCalledWith("/hubs/user", expect.objectContaining({ accessTokenFactory: expect.any(Function) }));
    expect(connection.invoke).not.toHaveBeenCalled();
  });

  it("forwards notifications", () => {
    const onNotification = vi.fn();
    connectUserFeed(options({ onNotification }));

    connection.handlers.get("Notification")!(item);

    expect(onNotification).toHaveBeenCalledWith(item);
  });

  it("forwards order status changes as (orderId, status)", () => {
    const onOrderStatus = vi.fn();
    connectUserFeed(options({ onOrderStatus }));

    connection.handlers.get("OrderStatusChanged")!("o1", "Paid");

    expect(onOrderStatus).toHaveBeenCalledWith("o1", "Paid");
  });

  it("asks for a resync after every automatic reconnect", async () => {
    const onResync = vi.fn();
    connectUserFeed(options({ onResync }));
    await vi.waitFor(() => expect(onResync).toHaveBeenCalledTimes(1));

    connection.reconnected!();

    await vi.waitFor(() => expect(onResync).toHaveBeenCalledTimes(2));
  });

  it("stops the connection", async () => {
    const feed = connectUserFeed(options());

    await feed.stop();

    expect(connection.stop).toHaveBeenCalled();
  });

  it("never stops retrying while reconnecting, with capped backoff", () => {
    connectUserFeed(options());

    const policy = reconnectPolicy.mock.calls.at(-1)![0] as { nextRetryDelayInMilliseconds: (c: { previousRetryCount: number }) => number | null };
    expect(policy.nextRetryDelayInMilliseconds({ previousRetryCount: 0 })).toBe(1_000);
    expect(policy.nextRetryDelayInMilliseconds({ previousRetryCount: 50 })).toBe(30_000);
  });

  describe("with fake timers", () => {
    beforeEach(() => {
      vi.useFakeTimers();
      connection.start.mockClear();
    });

    afterEach(() => vi.useRealTimers());

    it("starts again after the connection closed for good, then resyncs", async () => {
      const onResync = vi.fn();
      connectUserFeed(options({ onResync }));
      await vi.advanceTimersByTimeAsync(0);
      connection.start.mockClear();
      onResync.mockClear();

      connection.closed!();
      await vi.advanceTimersByTimeAsync(31_000);

      expect(connection.start).toHaveBeenCalled();
      expect(onResync).toHaveBeenCalled();
    });

    it("retries a first start that failed, backing off 1 s then 2 s", async () => {
      const startedAt: number[] = [];
      connection.start.mockImplementation(() => {
        startedAt.push(Date.now());
        return startedAt.length < 3 ? Promise.reject(new Error("hub down")) : Promise.resolve();
      });
      const onResync = vi.fn();
      const feed = connectUserFeed(options({ onResync }));

      await vi.advanceTimersByTimeAsync(10_000);

      expect(startedAt.slice(1).map((time, n) => time - startedAt[n]!)).toEqual([1_000, 2_000]);
      expect(onResync).toHaveBeenCalledTimes(1);
      connection.start.mockImplementation(() => Promise.resolve());
      await feed.stop();
    });

    it("does not start again after stop()", async () => {
      const feed = connectUserFeed(options());
      await vi.advanceTimersByTimeAsync(0);
      await feed.stop();
      connection.start.mockClear();

      await vi.advanceTimersByTimeAsync(60_000);

      expect(connection.start).not.toHaveBeenCalled();
    });
  });
});
