import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SearchSuggestions } from "@/lib/catalog/search";

const api = vi.hoisted(() => ({ fetchSuggestions: vi.fn() }));
vi.mock("@/lib/catalog/search", async (original) => ({ ...(await original<object>()), fetchSuggestions: api.fetchSuggestions }));

import { SUGGEST_DEBOUNCE_MS, useSuggestions } from "./use-suggestions";

const answer = (title: string): SearchSuggestions => ({
  events: [{ slug: title, title, startsAt: "2026-12-31T13:00:00Z", venueName: "V", city: "C", coverImageUrl: null }],
  artists: [],
  venues: [],
  cities: [],
});

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  vi.useRealTimers();
  api.fetchSuggestions.mockReset();
});

describe("useSuggestions", () => {
  it("waits for the typing to pause before asking", async () => {
    api.fetchSuggestions.mockResolvedValue(answer("son"));
    const { rerender } = renderHook(({ q }) => useSuggestions(q), { initialProps: { q: "s" } });

    rerender({ q: "so" });
    rerender({ q: "son" });
    await act(() => vi.advanceTimersByTimeAsync(SUGGEST_DEBOUNCE_MS));

    expect(api.fetchSuggestions).toHaveBeenCalledTimes(1);
    expect(api.fetchSuggestions.mock.calls[0][0]).toBe("son");
  });

  it("aborts the previous request when the query changes", async () => {
    const first = deferred<SearchSuggestions>();
    api.fetchSuggestions.mockReturnValueOnce(first.promise).mockResolvedValueOnce(answer("son tung"));
    const { rerender } = renderHook(({ q }) => useSuggestions(q), { initialProps: { q: "son" } });
    await act(() => vi.advanceTimersByTimeAsync(SUGGEST_DEBOUNCE_MS));

    rerender({ q: "son tung" });
    await act(() => vi.advanceTimersByTimeAsync(SUGGEST_DEBOUNCE_MS));

    const signal = api.fetchSuggestions.mock.calls[0][1] as AbortSignal;
    expect(signal.aborted).toBe(true);
  });

  it("never lets a slow earlier answer replace a later one", async () => {
    const slow = deferred<SearchSuggestions>();
    api.fetchSuggestions.mockReturnValueOnce(slow.promise).mockResolvedValueOnce(answer("son"));
    const { result, rerender } = renderHook(({ q }) => useSuggestions(q), { initialProps: { q: "so" } });
    await act(() => vi.advanceTimersByTimeAsync(SUGGEST_DEBOUNCE_MS));
    rerender({ q: "son" });
    await act(() => vi.advanceTimersByTimeAsync(SUGGEST_DEBOUNCE_MS));

    await act(async () => slow.resolve(answer("so")));

    expect(result.current.data?.events[0].title).toBe("son");
  });

  it("keeps the last results on screen while the next ones load", async () => {
    const next = deferred<SearchSuggestions>();
    api.fetchSuggestions.mockResolvedValueOnce(answer("son")).mockReturnValueOnce(next.promise);
    const { result, rerender } = renderHook(({ q }) => useSuggestions(q), { initialProps: { q: "son" } });
    await act(() => vi.advanceTimersByTimeAsync(SUGGEST_DEBOUNCE_MS));

    rerender({ q: "son t" });
    await act(() => vi.advanceTimersByTimeAsync(SUGGEST_DEBOUNCE_MS));

    expect(result.current.loading).toBe(true);
    expect(result.current.data?.events[0].title).toBe("son");
  });
});
