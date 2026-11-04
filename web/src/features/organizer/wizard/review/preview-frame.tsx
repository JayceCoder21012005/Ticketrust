"use client";

import { Monitor, Smartphone } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import type { EventDetail } from "@/lib/catalog/types";
import { cn } from "@/lib/utils";

export const PREVIEW_MESSAGE = "ticketrush:organizer-preview";
export const PREVIEW_READY = "ticketrush:organizer-preview-ready";

const DEVICES = {
  desktop: { label: "Desktop", icon: Monitor, width: 1280, height: 900 },
  phone: { label: "Điện thoại", icon: Smartphone, width: 390, height: 780 },
} as const;

type Device = keyof typeof DEVICES;

/**
 * The real spec 04 detail page in an iframe (so its breakpoints follow the frame's viewport), fed the draft through
 * same-origin postMessage. The iframe never signs in itself: a second refresh-token rotation in parallel would trip
 * the reuse detection of spec 03.
 */
export function PreviewFrame({ eventId, detail }: { eventId: string; detail: EventDetail }) {
  const frame = useRef<HTMLIFrameElement>(null);
  const box = useRef<HTMLDivElement>(null);
  const [device, setDevice] = useState<Device>("desktop");
  const [boxWidth, setBoxWidth] = useState(800);
  const size = DEVICES[device];
  const scale = Math.min(1, boxWidth / size.width);

  useEffect(() => {
    const send = () => frame.current?.contentWindow?.postMessage({ type: PREVIEW_MESSAGE, event: detail }, window.location.origin);
    send();
    const onMessage = (e: MessageEvent) => {
      if (e.origin === window.location.origin && e.source === frame.current?.contentWindow && e.data?.type === PREVIEW_READY) send();
    };
    window.addEventListener("message", onMessage);
    return () => window.removeEventListener("message", onMessage);
  }, [detail]);

  useEffect(() => {
    const element = box.current;
    if (!element || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(([entry]) => setBoxWidth(entry.contentRect.width));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  return (
    <div className="flex flex-col gap-3">
      <div role="radiogroup" aria-label="Kích thước xem trước" className="flex gap-2">
        {(Object.keys(DEVICES) as Device[]).map((key) => {
          const d = DEVICES[key];
          return (
            <button
              key={key}
              type="button"
              role="radio"
              aria-checked={device === key}
              onClick={() => setDevice(key)}
              className={cn(
                "flex items-center gap-2 rounded-md border px-3 py-1.5 text-sm",
                device === key ? "border-primary bg-primary/10 font-medium" : "text-muted-foreground hover:bg-muted",
              )}
            >
              <d.icon aria-hidden className="size-4" />
              {d.label}
            </button>
          );
        })}
      </div>
      <div ref={box} className="overflow-hidden rounded-xl border bg-muted/40" style={{ height: size.height * scale }}>
        <iframe
          ref={frame}
          title="Xem trước trang sự kiện"
          src={`/organizer/events/${eventId}/preview`}
          className="origin-top-left border-0 bg-background"
          style={{ width: size.width, height: size.height, transform: `scale(${scale})` }}
        />
      </div>
    </div>
  );
}
