import { describe, expect, it } from "vitest";
import { parseThemeTokens } from "./design-tokens";

const css = [
  "@theme inline {",
  "  --color-primary: var(--primary);",
  "}",
  ":root,",
  ".light {",
  "  --primary: oklch(0.55 0.22 285);",
  "  --background: oklch(1 0 0);",
  "}",
  ".dark {",
  "  --primary: oklch(0.65 0.2 285);",
  "}",
].join("\r\n");

describe("parseThemeTokens", () => {
  it("reads the light and dark token blocks, ignoring @theme mappings and CRLF", () => {
    expect(parseThemeTokens(css)).toEqual({
      light: { primary: "oklch(0.55 0.22 285)", background: "oklch(1 0 0)" },
      dark: { primary: "oklch(0.65 0.2 285)" },
    });
  });

  it("returns empty sets when a block is missing", () => {
    expect(parseThemeTokens("")).toEqual({ light: {}, dark: {} });
  });
});
