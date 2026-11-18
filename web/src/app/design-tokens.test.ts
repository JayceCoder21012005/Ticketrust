import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { contrastRatio } from "@/lib/contrast";
import { parseThemeTokens } from "@/lib/design-tokens";

const declared = parseThemeTokens(readFileSync(join(__dirname, "globals.css"), "utf8"));
const tokens = (selector: ":root" | ".dark") => (selector === ":root" ? declared.light : declared.dark);

const seatTokens = [
  ...Array.from({ length: 6 }, (_, i) => `seat-available-${i + 1}`),
  "seat-held",
  "seat-sold",
  "seat-selected",
];

describe.each([":root", ".dark"] as const)("design tokens (%s)", (selector) => {
  const t = tokens(selector);

  it.each(seatTokens)("%s passes 3:1 against the background", (name) => {
    expect(t[name], `--${name} is missing`).toBeDefined();
    expect(contrastRatio(t[name], t.background)).toBeGreaterThanOrEqual(3);
  });

  it.each([
    ["foreground", "background"],
    ["muted-foreground", "background"],
    ["primary-foreground", "primary"],
    ["card-foreground", "card"],
    // Links and accents use text-primary, on the page and inside cards (auth forms, spec 03).
    ["primary", "background"],
    ["primary", "card"],
  ])("%s on %s passes AA text contrast (4.5:1)", (fg, bg) => {
    expect(contrastRatio(t[fg], t[bg])).toBeGreaterThanOrEqual(4.5);
  });

  // The live accent (hot, selling fast, countdowns) is a UI graphic and the fill of short labels.
  it("live passes 3:1 against the background and the card", () => {
    expect(t.live, "--live is missing").toBeDefined();
    expect(contrastRatio(t.live, t.background)).toBeGreaterThanOrEqual(3);
    expect(contrastRatio(t.live, t.card)).toBeGreaterThanOrEqual(3);
  });

  it("live-foreground passes 4.5:1 on live", () => {
    expect(t["live-foreground"], "--live-foreground is missing").toBeDefined();
    expect(contrastRatio(t["live-foreground"], t.live)).toBeGreaterThanOrEqual(4.5);
  });

  it.each(["success", "warning", "danger", "info"])("has status colour %s", (name) => {
    expect(t[name]).toMatch(/^oklch\(/);
  });
});

describe("brand", () => {
  it("primary is the TicketRush violet and the radius is 0.75rem", () => {
    const light = tokens(":root");
    expect(light.primary).toBe("oklch(0.55 0.22 285)");
    expect(light.radius).toBe("0.75rem");
  });
});
