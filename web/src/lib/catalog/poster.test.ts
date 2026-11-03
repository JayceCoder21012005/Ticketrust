import { describe, expect, it } from "vitest";
import { contrastRatio } from "@/lib/contrast";
import { posterTheme } from "./poster";

describe("posterTheme", () => {
  it("is deterministic for a slug", () => {
    expect(posterTheme("son-tung-mtp-live", "concert")).toEqual(posterTheme("son-tung-mtp-live", "concert"));
  });

  it("varies between events of the same category", () => {
    expect(posterTheme("a-show", "concert").from).not.toBe(posterTheme("another-show", "concert").from);
  });

  it("keeps white title text readable (WCAG AA large text) on every category", () => {
    for (const category of ["concert", "festival", "theater", "sports", "comedy", "workshop", "unknown"]) {
      for (const slug of ["a", "son-tung-mtp-live", "marathon-da-nang-2026", "x".repeat(40)]) {
        const theme = posterTheme(slug, category);
        expect(contrastRatio("oklch(1 0 0)", theme.from)).toBeGreaterThanOrEqual(3);
        expect(contrastRatio("oklch(1 0 0)", theme.to)).toBeGreaterThanOrEqual(3);
      }
    }
  });
});
