import { describe, expect, it } from "vitest";
import { crispQrSize, qrPath, qrViewBoxSize, QUIET_ZONE } from "./qr-svg";

describe("qrPath", () => {
  it("draws each run of dark modules as one rectangle, inside a 4-module quiet zone", () => {
    const matrix = [
      [true, true, false],
      [false, true, true],
      [true, false, true],
    ];

    expect(QUIET_ZONE).toBe(4);
    expect(qrViewBoxSize(matrix)).toBe(11);
    expect(qrPath(matrix)).toBe("M4 4h2v1h-2zM5 5h2v1h-2zM4 6h1v1h-1zM6 6h1v1h-1z");
  });

  it("draws nothing for an all-light matrix", () => {
    expect(qrPath([[false, false]])).toBe("");
  });
});

describe("crispQrSize", () => {
  it("Module_size_is_an_integer: whole device pixels per module, as large as the box allows", () => {
    // 41 modules (version 4 with the quiet zone) in a 288 px box: 7 px each, 287 px in all.
    expect(crispQrSize(288, 41, 1)).toBe(287);
    expect(crispQrSize(310, 41, 2)).toBe(307.5);
    for (const [box, modules, dpr] of [
      [288, 41, 1],
      [300, 37, 1.5],
      [271, 41, 3],
      [320, 45, 2.625],
    ]) {
      const size = crispQrSize(box!, modules!, dpr!);
      const perModule = (size * dpr!) / modules!;
      expect(Math.abs(perModule - Math.round(perModule))).toBeLessThan(1e-9);
      expect(size).toBeLessThanOrEqual(box!);
      expect(size).toBeGreaterThan(box! - modules! / dpr!);
    }
  });

  it("keeps the box when it is smaller than one device pixel per module, or the ratio is unusable", () => {
    expect(crispQrSize(20, 41, 1)).toBe(20);
    expect(crispQrSize(288, 41, Number.NaN)).toBe(287);
    expect(crispQrSize(288, 41, 0)).toBe(287);
  });
});
