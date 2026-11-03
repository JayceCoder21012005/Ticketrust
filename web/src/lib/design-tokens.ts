export type TokenSet = Record<string, string>;

/**
 * Reads the declared light (`:root, .light`) and dark (`.dark`) token blocks from globals.css.
 * Works on the authored `oklch()` values: browsers report computed colours in other spaces (e.g. `lab()`),
 * so contrast must never be derived from `getComputedStyle`.
 */
export function parseThemeTokens(css: string): { light: TokenSet; dark: TokenSet } {
  const text = css.replaceAll("\r\n", "\n");
  return { light: block(text, "\n:root,\n.light {"), dark: block(text, "\n.dark {") };
}

function block(css: string, opener: string): TokenSet {
  const start = css.indexOf(opener);
  if (start < 0) {
    return {};
  }

  const body = css.slice(start, css.indexOf("}", start));
  return Object.fromEntries([...body.matchAll(/--([\w-]+):\s*([^;]+);/g)].map((match) => [match[1], match[2].trim()]));
}
