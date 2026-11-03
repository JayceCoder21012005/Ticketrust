import { describe, expect, it } from "vitest";
import { contrastRatio, parseOklch } from "./contrast";

describe("parseOklch", () => {
  it("reads lightness, chroma, hue and ignores alpha", () => {
    expect(parseOklch("oklch(0.55 0.22 285)")).toEqual({ l: 0.55, c: 0.22, h: 285 });
    expect(parseOklch("oklch(1 0 0 / 10%)")).toEqual({ l: 1, c: 0, h: 0 });
  });
});

describe("contrastRatio (WCAG 2.x)", () => {
  it("is 21:1 for white on black", () => {
    expect(contrastRatio("oklch(1 0 0)", "oklch(0 0 0)")).toBeCloseTo(21, 1);
  });

  it("is 1:1 for identical colours, in either order", () => {
    expect(contrastRatio("oklch(0.55 0.22 285)", "oklch(0.55 0.22 285)")).toBeCloseTo(1, 5);
  });

  it("is symmetric", () => {
    const a = contrastRatio("oklch(0.6 0 0)", "oklch(1 0 0)");
    expect(contrastRatio("oklch(1 0 0)", "oklch(0.6 0 0)")).toBeCloseTo(a, 10);
  });

  it("matches the known value for a neutral mid grey (L=0.6 → Y=0.216)", () => {
    expect(contrastRatio("oklch(0.6 0 0)", "oklch(1 0 0)")).toBeCloseTo(3.947, 2);
  });
});
