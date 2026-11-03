"use client";

import { useSyncExternalStore, type ReactNode } from "react";

/** Tailwind's `lg` breakpoint, where the listing shows its filter sidebar. */
const DESKTOP = "(min-width: 64rem)";

function subscribe(onChange: () => void) {
  const query = window.matchMedia(DESKTOP);
  query.addEventListener("change", onChange);
  return () => query.removeEventListener("change", onChange);
}

/**
 * Mounts the sidebar filters only on wide screens. Hidden with CSS alone, phones would still hydrate every
 * radio of the panel (blocking time on the slowest devices); phones use the filter dialog instead. The sidebar
 * column keeps its width, so filling it in after hydration moves nothing.
 */
export function DesktopOnly({ children }: { children: ReactNode }) {
  const desktop = useSyncExternalStore(
    subscribe,
    () => window.matchMedia(DESKTOP).matches,
    () => false,
  );

  return desktop ? children : null;
}
