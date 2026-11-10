"use client";

import { useEffect, useState, useSyncExternalStore } from "react";

const QUERY = "(prefers-reduced-motion: reduce)";
const PIECES = 24;
const SHOW_MS = 4_000;
const COLORS = ["bg-primary", "bg-success", "bg-warning", "bg-info"] as const;

const subscribe = (notify: () => void) => {
  const media = window.matchMedia?.(QUERY);
  media?.addEventListener("change", notify);
  return () => media?.removeEventListener("change", notify);
};
const prefersReducedMotion = () => window.matchMedia?.(QUERY).matches ?? true;

/**
 * A short, quiet burst of confetti behind the success message (spec 08). Decoration only: hidden from assistive
 * technology, never takes a click, and not rendered at all for a buyer who asked for reduced motion (or when the
 * browser cannot tell us).
 */
export function Confetti() {
  const reduced = useSyncExternalStore(subscribe, prefersReducedMotion, () => true);
  const [done, setDone] = useState(false);

  useEffect(() => {
    const timer = setTimeout(() => setDone(true), SHOW_MS);
    return () => clearTimeout(timer);
  }, []);

  if (reduced || done) {
    return null;
  }

  return (
    <div data-testid="confetti" aria-hidden className="pointer-events-none fixed inset-0 z-40 overflow-hidden">
      {Array.from({ length: PIECES }, (_, i) => (
        <span
          key={i}
          className={`confetti-piece ${COLORS[i % COLORS.length]}`}
          style={{ left: `${(i * 100) / PIECES + 2}%`, animationDelay: `${(i % 6) * 120}ms`, animationDuration: `${2_200 + (i % 5) * 300}ms` }}
        />
      ))}
    </div>
  );
}
