import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createQueueTransport, type WorkerLike } from "./transport";
import type { WorkerOut } from "./worker-protocol";

const handlers = () => ({ onStatus: vi.fn(), onUnauthorized: vi.fn(), onNotJoined: vi.fn(), onError: vi.fn() });

function fakeWorker() {
  const worker: WorkerLike = { postMessage: vi.fn(), terminate: vi.fn(), onmessage: null };
  const emit = (message: WorkerOut) => worker.onmessage?.({ data: message } as MessageEvent<WorkerOut>);
  return { worker, emit };
}

describe("worker transport", () => {
  it("forwards start, token, pollNow and stop to the worker", () => {
    const { worker } = fakeWorker();
    const transport = createQueueTransport(handlers(), () => worker);

    transport.start("evt", "tok");
    transport.setToken("tok2");
    transport.pollNow();
    transport.stop();

    expect(worker.postMessage).toHaveBeenNthCalledWith(1, { type: "start", eventId: "evt", token: "tok" });
    expect(worker.postMessage).toHaveBeenNthCalledWith(2, { type: "token", token: "tok2" });
    expect(worker.postMessage).toHaveBeenNthCalledWith(3, { type: "pollNow" });
    expect(worker.postMessage).toHaveBeenNthCalledWith(4, { type: "stop" });
    expect(worker.terminate).toHaveBeenCalled();
  });

  it("turns worker messages into handler calls", () => {
    const { worker, emit } = fakeWorker();
    const h = handlers();
    const transport = createQueueTransport(h, () => worker);
    transport.start("evt", "tok");
    const status = { status: "Waiting" } as never;

    emit({ type: "status", status });
    emit({ type: "unauthorized" });
    emit({ type: "notJoined" });
    emit({ type: "error", failures: 2 });

    expect(h.onStatus).toHaveBeenCalledWith(status);
    expect(h.onUnauthorized).toHaveBeenCalled();
    expect(h.onNotJoined).toHaveBeenCalled();
    expect(h.onError).toHaveBeenCalledWith(2);
  });
});

describe("inline fallback (no Web Worker available)", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("polls on the main thread and follows pollAfterMs", async () => {
    const body = {
      status: "Waiting", position: 5, aheadOfYou: 4, estimatedWaitSeconds: 1, opensAt: "2026-12-01T10:00:00Z",
      serverNow: "2026-12-01T10:00:00Z", pollAfterMs: 3000, admissionToken: null, tokenExpiresAt: null,
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(body), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    const h = handlers();
    const transport = createQueueTransport(h, () => null);

    transport.start("evt", "tok");
    await vi.advanceTimersByTimeAsync(0);
    expect(h.onStatus).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(3000);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    transport.stop();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
