import { describe, expect, it } from "vitest";
import { coverCropRect } from "./crop";

describe("coverCropRect", () => {
  it("takes the widest centred 16:9 band at zoom 1", () => {
    expect(coverCropRect(4000, 3000, 1, 0, 0)).toEqual({ sx: 0, sy: 375, sw: 4000, sh: 2250 });
  });

  it("uses the full height of an image wider than 16:9", () => {
    expect(coverCropRect(3200, 900, 1, 0, 0)).toEqual({ sx: 800, sy: 0, sw: 1600, sh: 900 });
  });

  it("halves the band at zoom 2", () => {
    expect(coverCropRect(4000, 3000, 2, 0, 0)).toEqual({ sx: 1000, sy: 937.5, sw: 2000, sh: 1125 });
  });

  it("moves to the edges at offset ±1 and never leaves the image", () => {
    expect(coverCropRect(4000, 3000, 2, -1, -1)).toEqual({ sx: 0, sy: 0, sw: 2000, sh: 1125 });
    expect(coverCropRect(4000, 3000, 2, 1, 1)).toEqual({ sx: 2000, sy: 1875, sw: 2000, sh: 1125 });
    expect(coverCropRect(4000, 3000, 2, 5, -5)).toEqual(coverCropRect(4000, 3000, 2, 1, -1));
  });

  it("clamps zoom to 1–3", () => {
    expect(coverCropRect(1600, 900, 0.2, 0, 0)).toEqual(coverCropRect(1600, 900, 1, 0, 0));
    expect(coverCropRect(1600, 900, 9, 0, 0)).toEqual(coverCropRect(1600, 900, 3, 0, 0));
  });
});
