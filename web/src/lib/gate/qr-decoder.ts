/** One frame of the camera, as pixels (what `getImageData` returns). */
export type Frame = { data: Uint8ClampedArray; width: number; height: number };

/** Reads a QR code from the camera's video element; resolves to its text, or null when the frame holds none. */
export type QrDecoder = {
  kind: "native" | "zxing";
  decode: (video: HTMLVideoElement) => Promise<string | null>;
};

/** Frames are scaled down to this width before ZXing reads them: a phone's full frame is far slower and no more readable. */
const MAX_FRAME_WIDTH = 640;

/** Luminance of every pixel (ITU-R BT.601 integer weights), the one channel ZXing reads. */
export function toLuminance(frame: Frame): Uint8ClampedArray {
  const { data, width, height } = frame;
  const luminance = new Uint8ClampedArray(width * height);
  for (let pixel = 0, offset = 0; pixel < luminance.length; pixel++, offset += 4) {
    luminance[pixel] = (data[offset] * 299 + data[offset + 1] * 587 + data[offset + 2] * 114) / 1000;
  }

  return luminance;
}

/**
 * ZXing's QR reader over one frame. ZXing is a dynamic import: only the gate screen on a browser without `BarcodeDetector`
 * (Safari, Firefox) ever downloads it. Never throws: an unreadable frame is `null`.
 */
export async function decodeFrameWithZxing(frame: Frame): Promise<string | null> {
  const { BinaryBitmap, HybridBinarizer, QRCodeReader, RGBLuminanceSource, DecodeHintType } = await import("@zxing/library");
  const bitmap = new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(toLuminance(frame), frame.width, frame.height)));
  try {
    return new QRCodeReader().decode(bitmap, new Map([[DecodeHintType.TRY_HARDER, true]])).getText();
  } catch {
    return null; // NotFoundException, ChecksumException, FormatException: nothing readable in this frame.
  }
}

type NativeDetector = { detect: (source: HTMLVideoElement) => Promise<{ rawValue: string }[]> };
type NativeDetectorConstructor = {
  new (options: { formats: string[] }): NativeDetector;
  getSupportedFormats?: () => Promise<string[]>;
};

async function nativeDetector(): Promise<NativeDetector | null> {
  const Ctor = (globalThis as { BarcodeDetector?: NativeDetectorConstructor }).BarcodeDetector;
  if (!Ctor) return null;
  try {
    const formats = (await Ctor.getSupportedFormats?.()) ?? ["qr_code"];
    return formats.includes("qr_code") ? new Ctor({ formats: ["qr_code"] }) : null;
  } catch {
    return null;
  }
}

function zxingDecoder(): QrDecoder {
  const canvas = document.createElement("canvas");
  return {
    kind: "zxing",
    async decode(video) {
      if (video.videoWidth === 0) return null;
      const scale = Math.min(1, MAX_FRAME_WIDTH / video.videoWidth);
      canvas.width = Math.round(video.videoWidth * scale);
      canvas.height = Math.round(video.videoHeight * scale);
      const context = canvas.getContext("2d", { willReadFrequently: true });
      if (!context) return null;
      context.drawImage(video, 0, 0, canvas.width, canvas.height);
      return decodeFrameWithZxing(context.getImageData(0, 0, canvas.width, canvas.height));
    },
  };
}

/** The browser's own detector when it reads QR codes, otherwise ZXing. */
export async function createQrDecoder(): Promise<QrDecoder> {
  const detector = await nativeDetector();
  if (!detector) return zxingDecoder();
  return {
    kind: "native",
    async decode(video) {
      try {
        return (await detector.detect(video))[0]?.rawValue ?? null;
      } catch {
        return null; // A frame the detector cannot take (the video is not ready yet).
      }
    },
  };
}
