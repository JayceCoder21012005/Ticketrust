export type CropRect = { sx: number; sy: number; sw: number; sh: number };

export const COVER_WIDTH = 1600;
export const COVER_HEIGHT = 900;
const RATIO = 16 / 9;

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

/**
 * The 16:9 part of an image to keep. Zoom 1 is the largest band that fits; zoom 3 a third of it. Offsets −1..1 slide
 * the band from one edge to the other, so it can never leave the image.
 */
export function coverCropRect(imageW: number, imageH: number, zoom: number, offsetX: number, offsetY: number): CropRect {
  const z = clamp(zoom, 1, 3);
  const wide = imageW / imageH > RATIO;
  const sw = (wide ? imageH * RATIO : imageW) / z;
  const sh = (wide ? imageH : imageW / RATIO) / z;
  const sx = ((imageW - sw) / 2) * (1 + clamp(offsetX, -1, 1));
  const sy = ((imageH - sh) / 2) * (1 + clamp(offsetY, -1, 1));
  return { sx, sy, sw, sh };
}

/** Draws the kept part at 1600×900 and encodes it as JPEG 0.85 (a few hundred KB, well under the 5 MB limit). */
export function renderCover(image: CanvasImageSource, rect: CropRect): Promise<Blob> {
  const canvas = document.createElement("canvas");
  canvas.width = COVER_WIDTH;
  canvas.height = COVER_HEIGHT;
  const context = canvas.getContext("2d");
  if (!context) {
    return Promise.reject(new Error("Canvas is not available."));
  }

  context.drawImage(image, rect.sx, rect.sy, rect.sw, rect.sh, 0, 0, COVER_WIDTH, COVER_HEIGHT);
  return new Promise((resolve, reject) =>
    canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error("Could not encode the cover."))), "image/jpeg", 0.85),
  );
}
