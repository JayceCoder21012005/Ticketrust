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

const { connectSeatFeed } = await import("./realtime");

describe("connectSeatFeed", () => {
  it("joins the event's group and asks for a resync once connected", async () => {
    const onResync = vi.fn();
    connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta: vi.fn(), onResync });

    await vi.waitFor(() => expect(connection.invoke).toHaveBeenCalledWith("JoinEvent", "e1"));
    await vi.waitFor(() => expect(onResync).toHaveBeenCalledTimes(1));
    expect(withUrl).toHaveBeenCalledWith("/hubs/events", expect.objectContaining({ accessTokenFactory: expect.any(Function) }));
  });

  it("forwards seat deltas", () => {
    const onDelta = vi.fn();
    connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta, onResync: vi.fn() });

    connection.handlers.get("SeatDelta")!({ from: 1, v: 1, held: [1], freed: [], sold: [], tiers: [] });

    expect(onDelta).toHaveBeenCalledWith(expect.objectContaining({ held: [1] }));
  });

  it("rejoins and resyncs after a reconnect", async () => {
    const onResync = vi.fn();
    connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta: vi.fn(), onResync });
    await vi.waitFor(() => expect(onResync).toHaveBeenCalledTimes(1));
    connection.invoke.mockClear();

    connection.reconnected!();

    await vi.waitFor(() => expect(connection.invoke).toHaveBeenCalledWith("JoinEvent", "e1"));
    await vi.waitFor(() => expect(onResync).toHaveBeenCalledTimes(2));
  });

  it("stops the connection", async () => {
    const feed = connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta: vi.fn(), onResync: vi.fn() });

    await feed.stop();

    expect(connection.stop).toHaveBeenCalled();
  });

  it("never stops retrying while reconnecting", () => {
    connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta: vi.fn(), onResync: vi.fn() });

    const policy = reconnectPolicy.mock.calls.at(-1)![0] as { nextRetryDelayInMilliseconds: (c: { previousRetryCount: number }) => number | null };
    expect(policy.nextRetryDelayInMilliseconds({ previousRetryCount: 50 })).toBeGreaterThan(0);
    expect(policy.nextRetryDelayInMilliseconds({ previousRetryCount: 50 })).toBeLessThanOrEqual(30_000);
  });

  it("starts again after the connection closed for good, then resyncs", async () => {
    vi.useFakeTimers();
    const onResync = vi.fn();
    connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta: vi.fn(), onResync });
    await vi.advanceTimersByTimeAsync(0);
    connection.start.mockClear();
    onResync.mockClear();

    connection.closed!();
    await vi.advanceTimersByTimeAsync(31_000);

    expect(connection.start).toHaveBeenCalled();
    expect(onResync).toHaveBeenCalled();
    vi.useRealTimers();
  });

  it("retries a first start that failed", async () => {
    vi.useFakeTimers();
    connection.start.mockClear();
    connection.start.mockRejectedValueOnce(new Error("hub down"));
    const onResync = vi.fn();
    connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta: vi.fn(), onResync });

    await vi.advanceTimersByTimeAsync(31_000);

    expect(connection.start.mock.calls.length).toBeGreaterThanOrEqual(2);
    expect(onResync).toHaveBeenCalled();
    vi.useRealTimers();
  });

  it("drops a connection that could not join the event, so the close handler starts it again", async () => {
    connection.stop.mockClear();
    connection.invoke.mockRejectedValueOnce(new Error("join failed"));
    connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta: vi.fn(), onResync: vi.fn() });

    await vi.waitFor(() => expect(connection.stop).toHaveBeenCalledTimes(1));
  });

  it("does the same when the join after an automatic reconnect fails", async () => {
    const onResync = vi.fn();
    connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta: vi.fn(), onResync });
    await vi.waitFor(() => expect(onResync).toHaveBeenCalled());
    connection.stop.mockClear();
    connection.invoke.mockRejectedValueOnce(new Error("join failed"));

    connection.reconnected!();

    await vi.waitFor(() => expect(connection.stop).toHaveBeenCalledTimes(1));
  });

  describe("when JoinEvent keeps failing", () => {
    const startTimes: number[] = [];

    const failJoins = (times: number) => {
      for (let n = 0; n < times; n++) connection.invoke.mockRejectedValueOnce(new Error("join failed"));
    };

    const gaps = () => startTimes.slice(1).map((time, n) => time - startTimes[n]);

    beforeEach(() => {
      vi.useFakeTimers();
      startTimes.length = 0;
      connection.start.mockClear();
      connection.stop.mockClear();
      connection.start.mockImplementation(() => {
        startTimes.push(Date.now());
        return Promise.resolve();
      });
    });

    afterEach(() => {
      connection.start.mockImplementation(() => Promise.resolve());
      vi.useRealTimers();
    });

    it("starts the connection again after each failed join, backing off 1 s, 2 s, 4 s", async () => {
      const onResync = vi.fn();
      failJoins(3);
      const feed = connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta: vi.fn(), onResync });

      await vi.advanceTimersByTimeAsync(10_000);

      expect(startTimes).toHaveLength(4);
      expect(gaps()).toEqual([1_000, 2_000, 4_000]);
      expect(onResync).toHaveBeenCalledTimes(1);
      await feed.stop();
    });

    it("backs off the same way when the join after an automatic reconnect fails", async () => {
      const onResync = vi.fn();
      const feed = connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta: vi.fn(), onResync });
      await vi.advanceTimersByTimeAsync(0);
      startTimes.length = 0;
      failJoins(2);

      const reconnectedAt = Date.now();
      connection.reconnected!();
      await vi.advanceTimersByTimeAsync(10_000);

      expect(startTimes.map((time) => time - reconnectedAt)).toEqual([1_000, 3_000]);
      expect(onResync).toHaveBeenCalledTimes(2);
      await feed.stop();
    });

    it("starts the next failure sequence from 1 s again once a join worked", async () => {
      const feed = connectSeatFeed("e1", { accessToken: () => Promise.resolve("tok"), onDelta: vi.fn(), onResync: vi.fn() });
      failJoins(2);
      await vi.advanceTimersByTimeAsync(10_000);
      startTimes.length = 0;
      failJoins(1);

      connection.reconnected!();
      await vi.advanceTimersByTimeAsync(1_500);

      expect(startTimes).toHaveLength(1);
      await feed.stop();
    });
  });
});
