import { describe, expect, it } from "vitest";
import { encodeQr } from "@/lib/tickets/qr-matrix";
import { decodeFrameWithZxing, toLuminance, type Frame } from "./qr-decoder";

/** Paints a QR matrix as an RGBA frame: `scale` pixels per module and a quiet zone of 4 modules, dark = black on white. */
function render(text: string, scale: number, tint: [number, number, number] = [0, 0, 0]): Frame {
  const matrix = encodeQr(text);
  const size = (matrix.length + 8) * scale;
  const data = new Uint8ClampedArray(size * size * 4).fill(255);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const row = Math.floor(y / scale) - 4;
      const col = Math.floor(x / scale) - 4;
      if (matrix[row]?.[col]) {
        data.set([...tint, 255], (y * size + x) * 4);
      }
    }
  }

  return { data, width: size, height: size };
}

describe("ZXing reads the codes the wallet draws", () => {
  const code = "TR1.AZLwDDOqe3uMzAEjRWeJqw.119223456.c2lnbmF0dXJl";

  it("reads a ticket code at the size of a phone screen", async () => {
    expect(await decodeFrameWithZxing(render(code, 6))).toBe(code);
  });

  it("reads a small, low-contrast rendering", async () => {
    expect(await decodeFrameWithZxing(render(code, 3, [60, 60, 90]))).toBe(code);
  });

  it("returns null for a frame with no code", async () => {
    const blank: Frame = { data: new Uint8ClampedArray(200 * 200 * 4).fill(255), width: 200, height: 200 };
    expect(await decodeFrameWithZxing(blank)).toBeNull();
  });

  it("converts RGBA to luminance", () => {
    const frame: Frame = { data: new Uint8ClampedArray([255, 255, 255, 255, 0, 0, 0, 255, 255, 0, 0, 255]), width: 3, height: 1 };
    expect([...toLuminance(frame)]).toEqual([255, 0, 76]);
  });
});
