export type Oklch = { l: number; c: number; h: number };

export function parseOklch(value: string): Oklch {
  const match = /oklch\(\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)/.exec(value);
  if (!match) {
    throw new Error(`Not an oklch() colour: ${value}`);
  }

  return { l: Number(match[1]), c: Number(match[2]), h: Number(match[3]) };
}

/** OKLCH → linear sRGB (Björn Ottosson's reference matrices), clipped to the sRGB gamut like browsers do. */
function toLinearSrgb({ l, c, h }: Oklch): [number, number, number] {
  const hue = (h * Math.PI) / 180;
  const a = c * Math.cos(hue);
  const b = c * Math.sin(hue);

  const l_ = (l + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const m_ = (l - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s_ = (l - 0.0894841775 * a - 1.291485548 * b) ** 3;

  const clip = (x: number) => Math.min(1, Math.max(0, x));
  return [
    clip(4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_),
    clip(-1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_),
    clip(-0.0041960863 * l_ - 0.7034186147 * m_ + 1.707614701 * s_),
  ];
}

export function relativeLuminance(colour: string): number {
  const [r, g, b] = toLinearSrgb(parseOklch(colour));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** WCAG contrast ratio, 1–21. Text needs ≥ 4.5, UI graphics such as seats ≥ 3 (WCAG 1.4.11). */
export function contrastRatio(a: string, b: string): number {
  const [light, dark] = [relativeLuminance(a), relativeLuminance(b)].sort((x, y) => y - x);
  return (light + 0.05) / (dark + 0.05);
}
