import { act, renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { useIsMobile } from "./use-mobile";

function mockViewport(width: number) {
  const listeners = new Set<() => void>();
  Object.defineProperty(window, "innerWidth", { configurable: true, value: width });
  window.matchMedia = vi.fn().mockImplementation(() => ({
    get matches() {
      return window.innerWidth < 768;
    },
    addEventListener: (_: string, cb: () => void) => listeners.add(cb),
    removeEventListener: (_: string, cb: () => void) => listeners.delete(cb),
  }));
  return {
    resize(next: number) {
      Object.defineProperty(window, "innerWidth", { configurable: true, value: next });
      listeners.forEach((cb) => cb());
    },
  };
}

describe("useIsMobile", () => {
  it("is true below 768px", () => {
    mockViewport(500);
    expect(renderHook(() => useIsMobile()).result.current).toBe(true);
  });

  it("is false from 768px", () => {
    mockViewport(1024);
    expect(renderHook(() => useIsMobile()).result.current).toBe(false);
  });

  it("follows viewport changes", () => {
    const viewport = mockViewport(1024);
    const { result } = renderHook(() => useIsMobile());

    act(() => viewport.resize(400));

    expect(result.current).toBe(true);
  });
});
