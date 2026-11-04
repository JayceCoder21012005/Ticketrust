import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { TIER_COLORS } from "./tier-colors";

function luminance(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

describe("tier colours", () => {
  it("are the api's TierColors.All, in the same order", () => {
    const source = readFileSync(resolve(process.cwd(), "../src/TicketRush.Domain/Events/TierColors.cs"), "utf8");
    const api = [...source.matchAll(/"(#[0-9a-f]{6})"/g)].map((m) => m[1]);

    expect(TIER_COLORS.map((c) => c.value)).toEqual(api);
  });

  it("keep white text at 4.5:1 or more", () => {
    for (const { value } of TIER_COLORS) {
      expect(1.05 / (luminance(value) + 0.05), value).toBeGreaterThanOrEqual(4.5);
    }
  });

  it("each have a Vietnamese name for the picker", () => {
    expect(new Set(TIER_COLORS.map((c) => c.name)).size).toBe(TIER_COLORS.length);
  });
});
