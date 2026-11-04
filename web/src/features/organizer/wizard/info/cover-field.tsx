"use client";

import { ImagePlus, Loader2 } from "lucide-react";
import { useEffect, useRef, useState, type PointerEvent } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Progress } from "@/components/ui/progress";
import { toFriendlyMessage } from "@/lib/errors";
import { uploadCover } from "../../api";
import { coverCropRect, renderCover } from "./crop";

const MAX_BYTES = 5 * 1024 * 1024;
const TYPES = ["image/jpeg", "image/png", "image/webp", "image/avif"];

type CoverFieldProps = { eventId: string; value: string | null; onChange: (url: string) => void };

/** Pick an image, crop it to 16:9 (drag to move, slider to zoom), upload it through the presigned URL. */
export function CoverField({ eventId, value, onChange }: CoverFieldProps) {
  const input = useRef<HTMLInputElement>(null);
  const [source, setSource] = useState<{ url: string; image: HTMLImageElement } | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => () => (source ? URL.revokeObjectURL(source.url) : undefined), [source]);

  const pick = (file: File | undefined) => {
    setError(null);
    if (!file) return;
    if (!TYPES.includes(file.type)) {
      setError("Chọn ảnh JPEG, PNG, WebP hoặc AVIF.");
      return;
    }

    const url = URL.createObjectURL(file);
    const image = new Image();
    image.onload = () => setSource({ url, image });
    image.onerror = () => setError("Không đọc được ảnh này.");
    image.src = url;
  };

  return (
    <div className="flex flex-col gap-2">
      <Label htmlFor="cover-input">Ảnh cover (16:9)</Label>
      <div className="relative aspect-video w-full max-w-xl overflow-hidden rounded-xl border bg-muted">
        {value ? (
          // eslint-disable-next-line @next/next/no-img-element -- organizer preview of an uploaded file, not a page image
          <img src={value} alt="Ảnh cover hiện tại" className="size-full object-cover" />
        ) : (
          <div className="flex size-full items-center justify-center text-sm text-muted-foreground">Chưa có ảnh cover</div>
        )}
      </div>
      <div className="flex items-center gap-3">
        <Button type="button" variant="outline" onClick={() => input.current?.click()}>
          <ImagePlus aria-hidden />
          {value ? "Đổi ảnh" : "Chọn ảnh"}
        </Button>
        <span className="text-sm text-muted-foreground">Ảnh được cắt về 1600×900, tối đa 5 MB.</span>
      </div>
      <input
        ref={input}
        id="cover-input"
        type="file"
        accept={TYPES.join(",")}
        className="sr-only"
        onChange={(e) => {
          pick(e.target.files?.[0]);
          e.target.value = "";
        }}
      />
      {error ? (
        <p role="alert" className="text-sm text-danger">
          {error}
        </p>
      ) : null}
      {source ? (
        <CoverCropper
          eventId={eventId}
          image={source.image}
          onClose={() => setSource(null)}
          onUploaded={(url) => {
            setSource(null);
            onChange(url);
          }}
        />
      ) : null}
    </div>
  );
}

function CoverCropper({ eventId, image, onClose, onUploaded }: { eventId: string; image: HTMLImageElement; onClose: () => void; onUploaded: (url: string) => void }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const drag = useRef<{ x: number; y: number; ox: number; oy: number } | null>(null);
  const [zoom, setZoom] = useState(1);
  const [offset, setOffset] = useState({ x: 0, y: 0 });
  const [progress, setProgress] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const rect = coverCropRect(image.naturalWidth, image.naturalHeight, zoom, offset.x, offset.y);

  useEffect(() => {
    const context = canvas.current?.getContext("2d");
    context?.drawImage(image, rect.sx, rect.sy, rect.sw, rect.sh, 0, 0, 480, 270);
  }, [image, rect.sx, rect.sy, rect.sw, rect.sh]);

  const move = (e: PointerEvent<HTMLCanvasElement>) => {
    if (!drag.current) return;
    // Dragging right shows more of the left side, like moving the photo under a frame.
    const dx = (e.clientX - drag.current.x) / 240;
    const dy = (e.clientY - drag.current.y) / 135;
    setOffset({ x: Math.max(-1, Math.min(1, drag.current.ox - dx)), y: Math.max(-1, Math.min(1, drag.current.oy - dy)) });
  };

  const upload = async () => {
    setError(null);
    try {
      const blob = await renderCover(image, rect);
      if (blob.size > MAX_BYTES) {
        setError("Ảnh sau khi cắt vẫn lớn hơn 5 MB.");
        return;
      }

      setProgress(0);
      onUploaded(await uploadCover(eventId, blob, setProgress));
    } catch (e) {
      setError(toFriendlyMessage(e));
      setProgress(null);
    }
  };

  return (
    <Dialog open onOpenChange={(open) => !open && progress === null && onClose()}>
      <DialogContent className="sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>Cắt ảnh cover</DialogTitle>
          <DialogDescription>Kéo ảnh để chọn phần hiển thị, dùng thanh trượt để phóng to.</DialogDescription>
        </DialogHeader>
        <canvas
          ref={canvas}
          width={480}
          height={270}
          aria-label="Xem trước ảnh cover"
          className="aspect-video w-full cursor-grab touch-none rounded-lg border active:cursor-grabbing"
          onPointerDown={(e) => {
            e.currentTarget.setPointerCapture(e.pointerId);
            drag.current = { x: e.clientX, y: e.clientY, ox: offset.x, oy: offset.y };
          }}
          onPointerMove={move}
          onPointerUp={() => (drag.current = null)}
        />
        <div className="flex flex-col gap-2">
          <Label htmlFor="cover-zoom">Phóng to</Label>
          <input id="cover-zoom" type="range" min={1} max={3} step={0.05} value={zoom} onChange={(e) => setZoom(Number(e.target.value))} />
        </div>
        {progress !== null ? <Progress value={progress * 100} aria-label="Đang tải ảnh lên" /> : null}
        {error ? (
          <p role="alert" className="text-sm text-danger">
            {error}
          </p>
        ) : null}
        <DialogFooter>
          <Button type="button" variant="ghost" onClick={onClose} disabled={progress !== null}>
            Huỷ
          </Button>
          <Button type="button" onClick={() => void upload()} disabled={progress !== null}>
            {progress !== null ? <Loader2 aria-hidden className="animate-spin" /> : null}
            Dùng ảnh này
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
