"use client";

import { cn } from "@/lib/utils";
import type { ScanView, Tone } from "@/lib/gate/describe-scan";

const TONES: Record<Tone, string> = {
  ok: "bg-green-700 text-white",
  warn: "bg-amber-400 text-black",
  bad: "bg-red-700 text-white",
};

/** The answer of a scan, over the whole screen. A tap anywhere closes it early; the button does the same for a keyboard. */
export function ResultOverlay({ view, onClose }: { view: ScanView; onClose: () => void }) {
  return (
    <div
      role="alert"
      data-testid="scan-result"
      data-tone={view.tone}
      onClick={onClose}
      className={cn("fixed inset-0 z-50 flex flex-col items-center justify-center gap-4 p-6 text-center", TONES[view.tone])}
    >
      <p className="text-4xl font-bold sm:text-5xl">{view.title}</p>
      {view.headline ? <p className="text-5xl font-extrabold leading-tight sm:text-7xl">{view.headline}</p> : null}
      {view.lines.map((line) => (
        <p key={line} className="text-xl font-medium">
          {line}
        </p>
      ))}
      <button type="button" className="mt-6 rounded-md border-2 border-current px-4 py-2 text-base font-semibold" onClick={onClose}>
        Đóng
      </button>
    </div>
  );
}
