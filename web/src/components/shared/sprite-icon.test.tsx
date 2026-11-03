import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { SPRITE_ICONS, SpriteIcon } from "./sprite-icon";

type IconData = { name: string; node: [string, Record<string, string>][] };

/** lucide-react's own drawing of an icon (the per-icon modules export it untyped). */
async function lucide(name: string): Promise<IconData> {
  const icon = (await import(/* @vite-ignore */ `lucide-react/dist/esm/icons/${name}.mjs`)) as { __iconData: IconData };
  return icon.__iconData;
}

const sprite = readFileSync(resolve(process.cwd(), "public/icons.svg"), "utf8");

describe("icon sprite", () => {
  it.each(SPRITE_ICONS)("draws %s exactly like lucide-react", async (name) => {
    const icon = await lucide(name);
    const symbol = new RegExp(`<symbol id="${icon.name}"[^>]*>([\\s\\S]*?)</symbol>`).exec(sprite)?.[1];
    expect(symbol, `symbol ${icon.name}`).toBeDefined();

    for (const [tag, attributes] of icon.node) {
      const drawn = Object.entries(attributes).filter(([k]) => k !== "key");
      const markup = `<${tag} ${drawn.map(([k, v]) => `${k}="${v}"`).join(" ")}/>`;
      expect(symbol).toContain(markup);
    }
  });

  it("has a symbol for every icon the component accepts", () => {
    for (const name of SPRITE_ICONS) {
      expect(sprite).toContain(`<symbol id="${name}"`);
    }
  });
});

describe("SpriteIcon", () => {
  it("references the cached sprite instead of inlining the drawing, and is decorative", () => {
    const { container } = render(<SpriteIcon name="map-pin" className="size-4" />);
    const svg = container.querySelector("svg")!;

    expect(svg).toHaveAttribute("aria-hidden", "true");
    expect(svg).toHaveAttribute("stroke", "currentColor");
    expect(svg.querySelector("use")).toHaveAttribute("href", "/icons.svg#map-pin");
    expect(svg.querySelectorAll("path")).toHaveLength(0);
  });
});
