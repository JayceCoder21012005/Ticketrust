import { afterEach, describe, expect, it, vi } from "vitest";
import { cropToSquare, squareCrop } from "./avatar-crop";

describe("squareCrop", () => {
  it.each([
    [1200, 800, { sx: 200, sy: 0, side: 800 }],
    [800, 1200, { sx: 0, sy: 200, side: 800 }],
    [500, 500, { sx: 0, sy: 0, side: 500 }],
  ])("takes the centred square of a %i × %i image", (width, height, expected) => {
    expect(squareCrop(width, height)).toEqual(expected);
  });
});

describe("cropToSquare", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("draws the centred square, upright, into a 512 px webp", async () => {
    const close = vi.fn();
    const bitmap = vi.fn(async () => ({ width: 1200, height: 800, close }));
    vi.stubGlobal("createImageBitmap", bitmap);
    const drawImage = vi.fn();
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue({ drawImage, imageSmoothingQuality: "low" } as unknown as CanvasRenderingContext2D);
    const toBlob = vi
      .spyOn(HTMLCanvasElement.prototype, "toBlob")
      .mockImplementation(function (this: HTMLCanvasElement, done: BlobCallback, type?: string) {
        done(new Blob([`${this.width}x${this.height}`], { type }));
      });
    const file = new File(["x"], "me.jpg", { type: "image/jpeg" });

    const blob = await cropToSquare(file);

    expect(bitmap).toHaveBeenCalledWith(file, { imageOrientation: "from-image" });
    expect(drawImage).toHaveBeenCalledWith(expect.anything(), 200, 0, 800, 800, 0, 0, 512, 512);
    expect(toBlob.mock.calls[0][1]).toBe("image/webp");
    expect(blob.type).toBe("image/webp");
    expect(await blob.text()).toBe("512x512");
    expect(close).toHaveBeenCalled();
  });

  it("never upscales a small image", async () => {
    vi.stubGlobal("createImageBitmap", vi.fn(async () => ({ width: 300, height: 200, close: vi.fn() })));
    const drawImage = vi.fn();
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue({ drawImage } as unknown as CanvasRenderingContext2D);
    vi.spyOn(HTMLCanvasElement.prototype, "toBlob").mockImplementation(function (this: HTMLCanvasElement, done: BlobCallback, type?: string) {
      done(new Blob([`${this.width}`], { type }));
    });

    const blob = await cropToSquare(new File(["x"], "me.png", { type: "image/png" }));

    expect(await blob.text()).toBe("200");
  });
});
