import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/** A stand-in for the HMAC (covered against the shared vectors in qr-code.test.ts): the step is readable in the code. */
const crypto = vi.hoisted(() => ({
  qrPayload: vi.fn(async (ticketId: string, _secret: Uint8Array, unixSeconds: number) => `TR1.${ticketId}.${Math.floor(unixSeconds / 15)}.sig`),
}));
vi.mock("@/lib/tickets/qr-code", async (importOriginal) => ({ ...(await importOriginal<typeof import("@/lib/tickets/qr-code")>()), qrPayload: crypto.qrPayload }));

const { useRotatingCode } = await import("./use-rotating-code");

const ID = "0192f0e0-aaaa-7bbb-8ccc-0123456789ab";
const SECRET = btoa(String.fromCharCode(...new Uint8Array(32).fill(7)));
/** 12:00:07 UTC on a 15-second grid: the step started 7 s ago (1_794_484_800 is a multiple of 15). */
const T0 = 1_794_484_800_000 + 7_000;
const stepAt = (ms: number) => Math.floor(ms / 15_000);

let visibility: DocumentVisibilityState = "visible";

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "Date"] });
  vi.setSystemTime(T0);
  visibility = "visible";
  vi.spyOn(document, "visibilityState", "get").mockImplementation(() => visibility);
  crypto.qrPayload.mockClear();
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const flush = () => act(() => vi.advanceTimersByTimeAsync(0));
const advance = (ms: number) => act(() => vi.advanceTimersByTimeAsync(ms));

function setVisibility(next: DocumentVisibilityState) {
  visibility = next;
  act(() => {
    document.dispatchEvent(new Event("visibilitychange"));
  });
}

describe("useRotatingCode", () => {
  it("Code_changes_exactly_at_each_15_second_boundary", async () => {
    const { result } = renderHook(() => useRotatingCode(ID, SECRET));
    await flush();

    const first = stepAt(T0);
    expect(result.current.step).toBe(first);
    expect(result.current.payload).toBe(`TR1.${ID}.${first}.sig`);
    expect(result.current.msLeft).toBe(8_000);

    // One millisecond before the boundary: same code.
    await advance(7_999);
    expect(result.current.payload).toBe(`TR1.${ID}.${first}.sig`);

    // On the boundary: the next step's code.
    await advance(1);
    expect(result.current.step).toBe(first + 1);
    expect(result.current.payload).toBe(`TR1.${ID}.${first + 1}.sig`);
    expect(result.current.msLeft).toBe(15_000);

    await advance(15_000);
    expect(result.current.payload).toBe(`TR1.${ID}.${first + 2}.sig`);
    // One generation per step, not one per tick.
    expect(crypto.qrPayload).toHaveBeenCalledTimes(3);
    expect(crypto.qrPayload.mock.calls.map((call) => Math.floor(call[2] / 15))).toEqual([first, first + 1, first + 2]);
  });

  it("schedules one timeout per boundary instead of a drifting interval", async () => {
    const setIntervalSpy = vi.spyOn(globalThis, "setInterval");
    renderHook(() => useRotatingCode(ID, SECRET));
    await flush();
    await advance(30_000);

    expect(setIntervalSpy).not.toHaveBeenCalled();
  });

  it("Time_uses_the_server_corrected_clock", async () => {
    // The device is 20 s slow: the server is already in the next step, 3 s before the one after.
    const offset = 20_000;
    const { result } = renderHook(() => useRotatingCode(ID, SECRET, offset));
    await flush();

    expect(result.current.step).toBe(stepAt(T0 + offset));
    expect(result.current.nowMs).toBe(T0 + offset);
    expect(result.current.msLeft).toBe(3_000);

    await advance(3_000);
    expect(result.current.payload).toBe(`TR1.${ID}.${stepAt(T0 + offset) + 1}.sig`);
  });

  it("Hidden_tab_pauses_and_resyncs_on_visibility", async () => {
    const { result } = renderHook(() => useRotatingCode(ID, SECRET));
    await flush();
    expect(crypto.qrPayload).toHaveBeenCalledTimes(1);

    setVisibility("hidden");
    await advance(60_000);
    // Nothing runs while hidden.
    expect(crypto.qrPayload).toHaveBeenCalledTimes(1);
    expect(result.current.step).toBe(stepAt(T0));

    // Back: the current step at once, without waiting for the next boundary.
    setVisibility("visible");
    await flush();
    expect(result.current.step).toBe(stepAt(T0 + 60_000));
    expect(result.current.payload).toBe(`TR1.${ID}.${stepAt(T0 + 60_000)}.sig`);
    expect(crypto.qrPayload).toHaveBeenCalledTimes(2);

    // And keeps rotating on the boundaries from there.
    await advance(8_000);
    expect(result.current.step).toBe(stepAt(T0 + 68_000));
  });

  it("Stale_async_generation_is_ignored", async () => {
    // The first step's HMAC is slow and only finishes after the next step's.
    let releaseSlow: (value: string) => void = () => undefined;
    crypto.qrPayload.mockImplementationOnce(() => new Promise<string>((resolve) => (releaseSlow = resolve)));

    const { result } = renderHook(() => useRotatingCode(ID, SECRET));
    await flush();
    expect(result.current.payload).toBeNull();

    await advance(8_000);
    expect(result.current.payload).toBe(`TR1.${ID}.${stepAt(T0) + 1}.sig`);

    await act(async () => releaseSlow(`TR1.${ID}.${stepAt(T0)}.sig`));
    expect(result.current.payload).toBe(`TR1.${ID}.${stepAt(T0) + 1}.sig`);
    expect(result.current.step).toBe(stepAt(T0) + 1);
  });

  it("Missing_crypto_subtle_shows_a_clear_error", async () => {
    vi.stubGlobal("crypto", { getRandomValues: globalThis.crypto.getRandomValues.bind(globalThis.crypto) });

    const { result } = renderHook(() => useRotatingCode(ID, SECRET));
    await flush();

    expect(result.current.error).toBe("insecure");
    expect(result.current.payload).toBeNull();
    expect(crypto.qrPayload).not.toHaveBeenCalled();
  });

  it("reports a failed generation instead of throwing", async () => {
    crypto.qrPayload.mockRejectedValueOnce(new RangeError("secret must be 32 bytes."));

    const { result } = renderHook(() => useRotatingCode(ID, SECRET));
    await flush();

    expect(result.current.error).toBe("failed");
    expect(result.current.payload).toBeNull();
  });

  it("stays idle without a secret", async () => {
    const { result } = renderHook(() => useRotatingCode(ID, null));
    await flush();

    expect(result.current.payload).toBeNull();
    expect(result.current.error).toBeNull();
    expect(crypto.qrPayload).not.toHaveBeenCalled();
  });
});
