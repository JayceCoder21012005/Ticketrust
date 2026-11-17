import { describe, expect, it } from "vitest";
import { createRepeatFilter } from "./repeat-filter";

describe("repeat filter", () => {
  it("lets a code through once while the camera keeps seeing it", () => {
    const filter = createRepeatFilter(1500);
    expect(filter.accept("A", 0)).toBe(true);
    expect(filter.accept("A", 200)).toBe(false);
    expect(filter.accept("A", 1600)).toBe(false); // seen 200 ms before this: still the same hold
    expect(filter.accept("A", 20_000)).toBe(true); // gone for a long while, then shown again
  });

  it("lets a different code through at once", () => {
    const filter = createRepeatFilter();
    expect(filter.accept("A", 0)).toBe(true);
    expect(filter.accept("B", 100)).toBe(true);
    expect(filter.accept("A", 200)).toBe(true);
  });

  it("keeps ignoring the last code after a pause when it is touched", () => {
    const filter = createRepeatFilter(1500);
    filter.accept("A", 0);
    filter.touch(5000);
    expect(filter.accept("A", 5200)).toBe(false);
  });

  it("forgets the last code on reset", () => {
    const filter = createRepeatFilter();
    filter.accept("A", 0);
    filter.reset();
    expect(filter.accept("A", 100)).toBe(true);
  });
});
