import { act, renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { fetchSeatMap } from "./availability";
import { createSeatStore } from "./seat-store";
import { useSeat, useStoreRevision } from "./use-seat";

describe("useSeat", () => {
  it("follows one seat's state", () => {
    const store = createSeatStore(5);
    const { result } = renderHook(() => useSeat(store, 2));
    expect(result.current).toBe("free");

    act(() => {
      store.toggle(2);
    });

    expect(result.current).toBe("selected");
  });

  it("re-renders whole-map readers on any change", () => {
    const store = createSeatStore(5);
    const { result } = renderHook(() => useStoreRevision(store));
    const before = result.current;

    act(() => {
      store.toggle(1);
    });

    expect(result.current).toBeGreaterThan(before);
  });
});

describe("fetchSeatMap", () => {
  it("reads the event's static layout", async () => {
    const map = { eventId: "e1", version: "v", sections: [], seats: [{ index: 0, section: "A", row: "1", number: "1", x: "10.5", y: 20, tierId: "t" }] };
    const fetchMock = vi.fn(() => Promise.resolve(new Response(JSON.stringify(map), { status: 200 })));
    vi.stubGlobal("fetch", fetchMock);

    const result = await fetchSeatMap("e1");

    expect(fetchMock).toHaveBeenCalledWith("/api/v1/events/e1/seat-map", expect.anything());
    expect(result.seats[0].x).toBe(10.5);
    vi.unstubAllGlobals();
  });
});
