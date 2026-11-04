import { describe, expect, it } from "vitest";
import { MAX_SCALE, MIN_SCALE, pan, zoomAt } from "./use-pan-zoom";

describe("zoomAt", () => {
  it("keeps the point under the cursor in place", () => {
    const view = zoomAt({ x: 0, y: 0, k: 1 }, 2, 100, 50);

    expect(view.k).toBe(2);
    // The world point (100, 50) maps to screen (100, 50) both before and after.
    expect(view.x + 100 * view.k).toBe(100);
    expect(view.y + 50 * view.k).toBe(50);
  });

  it(`clamps the scale between ${MIN_SCALE}× and ${MAX_SCALE}×`, () => {
    expect(zoomAt({ x: 0, y: 0, k: 7 }, 4, 0, 0).k).toBe(MAX_SCALE);
    expect(zoomAt({ x: 0, y: 0, k: 0.6 }, 0.1, 0, 0).k).toBe(MIN_SCALE);
  });
});

describe("pan", () => {
  it("moves the view by the drag distance", () => {
    expect(pan({ x: 10, y: 20, k: 3 }, 5, -7)).toEqual({ x: 15, y: 13, k: 3 });
  });
});
