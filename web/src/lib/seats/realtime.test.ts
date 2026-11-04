import { describe, expect, it, vi } from "vitest";

type Handler = (...args: unknown[]) => void;

const connection = {
  handlers: new Map<string, Handler>(),
  reconnected: undefined as (() => void) | undefined,
  closed: undefined as (() => void) | undefined,
  start: vi.fn(() => Promise.resolve()),
  stop: vi.fn(() => Promise.resolve()),
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
});
