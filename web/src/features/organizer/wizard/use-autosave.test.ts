import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";
import { useAutosave, type AutosaveStatus } from "./use-autosave";

type Props = { value: string; valid: boolean };

function setup(save: (value: string) => Promise<void>, initial: Props = { value: "a", valid: true }, onLeftStatus?: (status: AutosaveStatus) => void) {
  return renderHook((props: Props) => useAutosave({ value: props.value, valid: props.valid, save, onLeftStatus }), { initialProps: initial });
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

const flush = () => act(async () => {
  await vi.runOnlyPendingTimersAsync();
});

describe("useAutosave", () => {
  it("does not save the value it started with", async () => {
    const save = vi.fn(async () => {});
    setup(save);

    await flush();

    expect(save).not.toHaveBeenCalled();
  });

  it("saves 800 ms after the last change", async () => {
    const save = vi.fn(async () => {});
    const { result, rerender } = setup(save);

    rerender({ value: "ab", valid: true });
    await act(async () => vi.advanceTimersByTimeAsync(500));
    rerender({ value: "abc", valid: true });
    await act(async () => vi.advanceTimersByTimeAsync(799));
    expect(save).not.toHaveBeenCalled();

    await act(async () => vi.advanceTimersByTimeAsync(1));
    expect(save).toHaveBeenCalledExactlyOnceWith("abc");
    expect(result.current.state).toBe("saved");
    expect(result.current.savedAt).toBeInstanceOf(Date);
  });

  it("never saves an invalid value and says so", async () => {
    const save = vi.fn(async () => {});
    const { result, rerender } = setup(save);

    rerender({ value: "", valid: false });
    await flush();

    expect(save).not.toHaveBeenCalled();
    expect(result.current.state).toBe("invalid");
  });

  it("queues exactly one more save for changes made while saving", async () => {
    let finish: () => void = () => {};
    const save = vi.fn(() => new Promise<void>((resolve) => (finish = resolve)));
    const { rerender } = setup(save);

    rerender({ value: "b", valid: true });
    await flush();
    expect(save).toHaveBeenCalledTimes(1);

    rerender({ value: "c", valid: true });
    await flush();
    rerender({ value: "d", valid: true });
    await flush();
    expect(save).toHaveBeenCalledTimes(1);

    await act(async () => finish());
    await flush();
    expect(save).toHaveBeenCalledTimes(2);
    expect(save).toHaveBeenLastCalledWith("d");
  });

  it("flags a concurrent edit as a conflict", async () => {
    const save = vi.fn(async () => {
      throw new ApiError(409, { code: "event.concurrent_edit" });
    });
    const { result, rerender } = setup(save);

    rerender({ value: "b", valid: true });
    await flush();

    expect(result.current.state).toBe("error");
    expect(result.current.conflict).toBe(true);
  });

  it("retry() saves the pending value again after a failure", async () => {
    const save = vi.fn().mockRejectedValueOnce(new ApiError(500, {})).mockResolvedValue(undefined);
    const { result, rerender } = setup(save);

    rerender({ value: "b", valid: true });
    await flush();
    expect(result.current.state).toBe("error");

    await act(async () => result.current.retry());
    await flush();

    expect(save).toHaveBeenCalledTimes(2);
    expect(result.current.state).toBe("saved");
  });

  it("saves a pending change right away when the step is left", async () => {
    const save = vi.fn(async () => {});
    const { rerender, unmount } = setup(save);

    rerender({ value: "b", valid: true });
    unmount();
    await flush();

    expect(save).toHaveBeenCalledExactlyOnceWith("b");
  });

  it("does not save an invalid change when the step is left", async () => {
    const save = vi.fn(async () => {});
    const { rerender, unmount } = setup(save);

    rerender({ value: "", valid: false });
    unmount();
    await flush();

    expect(save).not.toHaveBeenCalled();
  });

  it("discard() forgets the pending change: no debounced save and none on leaving", async () => {
    const save = vi.fn(async () => {});
    const { result, rerender, unmount } = setup(save);

    rerender({ value: "b", valid: true });
    act(() => result.current.discard());
    await flush();
    unmount();
    await flush();

    expect(save).not.toHaveBeenCalled();
  });

  it("doesn't save again on leaving after a conflict, and reports the conflict instead", async () => {
    const save = vi.fn(async () => {
      throw new ApiError(409, { code: "event.concurrent_edit" });
    });
    const onLeft = vi.fn();
    const { rerender, unmount } = setup(save, undefined, onLeft);

    rerender({ value: "b", valid: true });
    await flush();
    unmount();
    await flush();

    expect(save).toHaveBeenCalledTimes(1);
    expect(onLeft).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ state: "error", conflict: true }));
  });

  it("reports how a save that finished after leaving the step went, and its retry", async () => {
    const save = vi.fn().mockRejectedValueOnce(new ApiError(500, {})).mockResolvedValue(undefined);
    const onLeft = vi.fn();
    const { rerender, unmount } = setup(save, undefined, onLeft);

    rerender({ value: "b", valid: true });
    unmount();
    await flush();
    expect(onLeft).toHaveBeenLastCalledWith(expect.objectContaining({ state: "error", conflict: false }));

    await act(async () => (onLeft.mock.lastCall![0] as AutosaveStatus).retry());
    await flush();

    expect(save).toHaveBeenCalledTimes(2);
    expect(onLeft).toHaveBeenLastCalledWith(expect.objectContaining({ state: "saved" }));
  });

  it("asks before closing the tab only while a change is unsaved", async () => {
    const add = vi.spyOn(window, "addEventListener");
    const remove = vi.spyOn(window, "removeEventListener");
    const save = vi.fn(async () => {});
    const { rerender } = setup(save);
    expect(add.mock.calls.filter(([type]) => type === "beforeunload")).toHaveLength(0);

    rerender({ value: "b", valid: true });
    expect(add.mock.calls.filter(([type]) => type === "beforeunload")).toHaveLength(1);

    await flush();
    expect(remove.mock.calls.filter(([type]) => type === "beforeunload")).toHaveLength(1);
  });
});
