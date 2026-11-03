/**
 * Generated poster for events without a cover image (design doc §3): a gradient in the category's hue, nudged
 * per event so neighbouring cards differ. No image request, so the title text is the LCP element.
 */
const CATEGORY_HUES: Record<string, number> = {
  concert: 290,
  festival: 35,
  theater: 355,
  sports: 155,
  comedy: 75,
  workshop: 235,
};

export type PosterTheme = { from: string; to: string; angle: number };

function hash(value: string): number {
  let h = 2166136261;
  for (let i = 0; i < value.length; i++) {
    h = Math.imul(h ^ value.charCodeAt(i), 16777619);
  }

  return h >>> 0;
}

export function posterTheme(slug: string, category: string): PosterTheme {
  const seed = hash(slug);
  const hue = ((CATEGORY_HUES[category] ?? 260) + (seed % 41) - 20 + 360) % 360;
  // Lightness ≤ 0.5 keeps white text above 4.5:1 on the light end of the gradient.
  return {
    from: `oklch(0.5 0.16 ${hue})`,
    to: `oklch(0.3 0.12 ${(hue + 40) % 360})`,
    angle: 120 + (seed % 90),
  };
}
