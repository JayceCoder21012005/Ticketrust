import { describe, expect, it, vi } from "vitest";

type Handler = (...args: unknown[]) => void;

const connection = {
  handlers: new Map<string, Handler>(),
  reconnected: undefined as (() => void) | undefined,
  start: vi.fn(() => Promise.resolve()),
  stop: vi.fn(() => Promise.resolve()),
  invoke: vi.fn(() => Promise.resolve()),
  on(name: string, handler: Handler) {
    this.handlers.set(name, handler);
  },
  onreconnected(handler: () => void) {
    this.reconnected = handler;
  },
};

const withUrl = vi.fn();
vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: class {
    withUrl(url: string, options: unknown) {
      withUrl(url, options);
      return this;
    }
    withAutomaticReconnect() {
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
});
