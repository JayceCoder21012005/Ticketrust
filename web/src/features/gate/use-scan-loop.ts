"use client";

import { useEffect, useEffectEvent, useState, type RefObject } from "react";
import { createQrDecoder } from "@/lib/gate/qr-decoder";
import { createRepeatFilter } from "@/lib/gate/repeat-filter";

const INTERVAL_MS = 150;

/**
 * Reads the camera about 6 times a second and calls `onCode` once for each new code (see the repeat filter). While `paused`
 * (the result of the last scan is on screen) nothing is read; when it resumes, the code still in front of the camera does not
 * count as new.
 */
export function useScanLoop({
  videoRef,
  active,
  paused,
  onCode,
}: {
  videoRef: RefObject<HTMLVideoElement | null>;
  active: boolean;
  paused: boolean;
  onCode: (payload: string) => void;
}) {
  const [filter] = useState(() => createRepeatFilter());
  const emit = useEffectEvent(onCode);
  const isPaused = useEffectEvent(() => paused);

  useEffect(() => {
    if (!paused) filter.touch(Date.now());
  }, [paused, filter]);

  useEffect(() => {
    if (!active) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    async function run() {
      const decoder = await createQrDecoder();
      const tick = async () => {
        const video = videoRef.current;
        if (!stopped && video && video.readyState >= 2 && !isPaused()) {
          const text = await decoder.decode(video);
          if (!stopped && !isPaused() && text && filter.accept(text, Date.now())) emit(text);
        }

        if (!stopped) timer = setTimeout(tick, INTERVAL_MS);
      };

      void tick();
    }

    void run();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [active, filter, videoRef]);
}
