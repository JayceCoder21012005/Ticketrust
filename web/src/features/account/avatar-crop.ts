/** The centred square of a `width × height` image. */
export function squareCrop(width: number, height: number): { sx: number; sy: number; side: number } {
  const side = Math.min(width, height);
  return { sx: Math.round((width - side) / 2), sy: Math.round((height - side) / 2), side };
}

/**
 * Avatars are shown in circles everywhere (UI refresh): crop the centred square before upload, upright (EXIF orientation applied),
 * at most `size` px, as WebP. The upload is then small and always looks the way the preview did.
 */
export async function cropToSquare(file: File, size = 512): Promise<Blob> {
  const bitmap = await createImageBitmap(file, { imageOrientation: "from-image" });
  try {
    const { sx, sy, side } = squareCrop(bitmap.width, bitmap.height);
    const target = Math.min(size, side);
    const canvas = document.createElement("canvas");
    canvas.width = target;
    canvas.height = target;
    const context = canvas.getContext("2d");
    if (!context) throw new Error("Canvas 2D is not available");
    context.imageSmoothingQuality = "high";
    context.drawImage(bitmap, sx, sy, side, side, 0, 0, target, target);
    return await new Promise<Blob>((resolve, reject) =>
      canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error("Could not encode the avatar"))), "image/webp", 0.9),
    );
  } finally {
    bitmap.close();
  }
}
