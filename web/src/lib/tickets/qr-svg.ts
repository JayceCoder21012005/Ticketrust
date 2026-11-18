/** ISO 18004 asks for 4 light modules around the symbol; scanners need it to find the finder patterns. */
export const QUIET_ZONE = 4;

/** The side of the drawing in modules, quiet zone included. */
export function qrViewBoxSize(matrix: readonly (readonly boolean[])[]): number {
  return matrix.length + 2 * QUIET_ZONE;
}

/**
 * One SVG path for the dark modules: each horizontal run of a row is a rectangle (`M x y h n v1 h -n z`), offset by
 * the quiet zone. Kept tiny and pure so the e2e test can compute the same path for an expected code. No encoder here:
 * the encoder is loaded on demand by the wallet.
 */
export function qrPath(matrix: readonly (readonly boolean[])[]): string {
  const parts: string[] = [];
  matrix.forEach((row, y) => {
    let x = 0;
    while (x < row.length) {
      if (!row[x]) {
        x++;
        continue;
      }
      const start = x;
      while (x < row.length && row[x]) x++;
      parts.push(`M${start + QUIET_ZONE} ${y + QUIET_ZONE}h${x - start}v1h-${x - start}z`);
    }
  });
  return parts.join("");
}

/**
 * The side, in CSS px, at which a symbol of `modules` modules is drawn crisp in a `boxCssPx` box: every module covers
 * a whole number of DEVICE pixels (`floor(box × dpr / modules) × modules / dpr`), so no edge is anti-aliased at any
 * device pixel ratio. At a ratio of 1 this is `floor(box / modules) × modules`. A box too small for that keeps its size.
 */
export function crispQrSize(boxCssPx: number, modules: number, devicePixelRatio = 1): number {
  const ratio = Number.isFinite(devicePixelRatio) && devicePixelRatio > 0 ? devicePixelRatio : 1;
  const perModule = Math.floor((boxCssPx * ratio) / modules);
  return perModule > 0 ? (perModule * modules) / ratio : boxCssPx;
}
