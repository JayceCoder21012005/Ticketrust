"use client";

import { useEffect } from "react";

/**
 * One shared `pointermove` listener for every `[data-spotlight]` element (the event cards): it writes the pointer position into
 * the hovered element's `--x`/`--y`, which its border glow follows. Fine pointers only, never under reduced motion, and the work
 * is batched to one write per frame.
 */
export function CardSpotlight() {
  useEffect(() => {
    if (!window.matchMedia("(pointer: fine) and (prefers-reduced-motion: no-preference)").matches) return;
    let frame = 0;
    const onMove = (event: PointerEvent) => {
      const target = (event.target as Element | null)?.closest?.<HTMLElement>("[data-spotlight]");
      if (!target || frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        const box = target.getBoundingClientRect();
        target.style.setProperty("--x", `${event.clientX - box.left}px`);
        target.style.setProperty("--y", `${event.clientY - box.top}px`);
      });
    };
    document.addEventListener("pointermove", onMove, { passive: true });
    return () => {
      document.removeEventListener("pointermove", onMove);
      cancelAnimationFrame(frame);
    };
  }, []);

  return null;
}
