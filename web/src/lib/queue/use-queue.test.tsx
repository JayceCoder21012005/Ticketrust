import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";
import { loadAdmission } from "./admission-store";
import type { QueueTransport, TransportHandlers } from "./transport";
import type { QueueStatus } from "./types";
import { useQueue } from "./use-queue";

const status = (overrides: Partial<QueueStatus> = {}): QueueStatus => ({
  status: "Waiting",
  position: 800,
  aheadOfYou: 799,
  estimatedWaitSeconds: 60,
  opensAt: "2026-12-01T10:00:00Z",
  serverNow: "2026-12-01T10:00:00Z",
  pollAfterMs: 4_000,
  admissionToken: null,
  tokenExpiresAt: null,
  ...overrides,
});

function setup(joinResult: QueueStatus | Error = status()) {
  let handlers!: TransportHandlers;
  const transport: QueueTransport = { start: vi.fn(), setToken: vi.fn(), pollNow: vi.fn(), stop: vi.fn() };
  const join = vi.fn(async () => {
    if (joinResult instanceof Error) throw joinResult;
    return joinResult;
  });
  const leave = vi.fn(async () => {});
  const refresh = vi.fn(async () => "fresh-token");
  const hook = renderHook(() =>
    useQueue("evt-1", "show", {
      createTransport: (h) => {
        handlers = h;
        return transport;
      },
      join,
      leave,
      getToken: () => "access-token",
      refresh,
    }),
  );
  return { hook, transport, join, leave, refresh, handlers: () => handlers };
}

describe("useQueue", () => {
  beforeEach(() => {
    localStorage.clear();
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-12-01T10:00:05Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("starts idle, joins, and then polls with the access token", async () => {
    const { hook, transport, join } = setup();
    expect(hook.result.current.phase).toBe("idle");

    await act(() => hook.result.current.join("VIP"));

    expect(join).toHaveBeenCalledWith("evt-1", "VIP", undefined);
    expect(hook.result.current.phase).toBe("active");
    expect(hook.result.current.status?.position).toBe(800);
    expect(transport.start).toHaveBeenCalledWith("evt-1", "access-token");
  });

  it("measures the server clock offset from serverNow, not the device clock", async () => {
    const { hook } = setup(status({ serverNow: "2026-12-01T10:10:05Z" }));

    await act(() => hook.result.current.join());

    expect(hook.result.current.offsetMs).toBe(10 * 60_000);
  });

  it("remembers the first waiting position as the progress baseline", async () => {
    const { hook, handlers } = setup(status({ position: 800 }));
    await act(() => hook.result.current.join());

    act(() => handlers().onStatus(status({ position: 300 })));

    expect(hook.result.current.startPosition).toBe(800);
    expect(hook.result.current.status?.position).toBe(300);
  });

  it("stores the admission when admitted and drops it when expired", async () => {
    const { hook, handlers } = setup();
    await act(() => hook.result.current.join());

    act(() => handlers().onStatus(status({ status: "Admitted", position: null, admissionToken: "adm", tokenExpiresAt: "2026-12-01T10:10:00Z" })));
    expect(loadAdmission()).toEqual({ eventId: "evt-1", slug: "show", token: "adm", expiresAt: "2026-12-01T10:10:00Z" });

    act(() => handlers().onStatus(status({ status: "Expired", position: null })));
    expect(loadAdmission()).toBeNull();
  });

  it("does not start polling when the sale is already closed", async () => {
    const { hook, transport } = setup(status({ status: "Closed", position: null }));

    await act(() => hook.result.current.join());

    expect(hook.result.current.status?.status).toBe("Closed");
    expect(transport.start).not.toHaveBeenCalled();
  });

  it("refreshes the access token when the poller reports 401", async () => {
    const { hook, handlers, refresh, transport } = setup();
    await act(() => hook.result.current.join());

    await act(async () => handlers().onUnauthorized());

    expect(refresh).toHaveBeenCalled();
    expect(transport.setToken).toHaveBeenCalledWith("fresh-token");
  });

  it("surfaces the api error code when joining fails", async () => {
    const { hook, transport } = setup(new ApiError(403, { code: "queue.invalid_access_code" }));

    await act(() => hook.result.current.join("nope"));

    expect(hook.result.current.phase).toBe("failed");
    expect(hook.result.current.error?.code).toBe("queue.invalid_access_code");
    expect(transport.start).not.toHaveBeenCalled();
  });

  it("asks to join again when the server forgot the user", async () => {
    const { hook, handlers } = setup();
    await act(() => hook.result.current.join());

    act(() => handlers().onNotJoined());

    expect(hook.result.current.phase).toBe("failed");
    expect(hook.result.current.error?.code).toBe("queue.not_joined");
  });

  it("counts connection problems and clears them on the next status", async () => {
    const { hook, handlers } = setup();
    await act(() => hook.result.current.join());

    act(() => handlers().onError(3));
    expect(hook.result.current.connectionIssues).toBe(3);
    act(() => handlers().onStatus(status()));
    expect(hook.result.current.connectionIssues).toBe(0);
  });

  it("leave() tells the api, stops polling, forgets the admission and goes idle", async () => {
    const { hook, handlers, leave, transport } = setup();
    await act(() => hook.result.current.join());
    act(() => handlers().onStatus(status({ status: "Admitted", position: null, admissionToken: "adm", tokenExpiresAt: "2026-12-01T10:10:00Z" })));

    await act(() => hook.result.current.leave());

    expect(leave).toHaveBeenCalledWith("evt-1");
    expect(transport.stop).toHaveBeenCalled();
    expect(loadAdmission()).toBeNull();
    expect(hook.result.current.phase).toBe("idle");
  });

  it("does not start a worker when the page was left while the join was still in flight", async () => {
    let finishJoin: (status: QueueStatus) => void = () => {};
    const transport: QueueTransport = { start: vi.fn(), setToken: vi.fn(), pollNow: vi.fn(), stop: vi.fn() };
    const hook = renderHook(() =>
      useQueue("evt-1", "show", {
        createTransport: () => transport,
        join: () => new Promise<QueueStatus>((resolve) => (finishJoin = resolve)),
        leave: vi.fn(),
        getToken: () => "access-token",
        refresh: vi.fn(),
      }),
    );

    let joining: Promise<void> = Promise.resolve();
    act(() => {
      joining = hook.result.current.join();
    });
    hook.unmount();
    await act(async () => {
      finishJoin(status());
      await joining;
    });

    expect(transport.start).not.toHaveBeenCalled();
    expect(loadAdmission()).toBeNull();
  });

  it("stops polling on unmount", async () => {
    const { hook, transport } = setup();
    await act(() => hook.result.current.join());

    hook.unmount();

    expect(transport.stop).toHaveBeenCalled();
  });
});
