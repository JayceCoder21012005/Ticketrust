import { cn } from "@/lib/utils";

/** Icons in `public/icons.svg`. */
export const SPRITE_ICONS = ["calendar-days", "map-pin"] as const;
export type SpriteIconName = (typeof SPRITE_ICONS)[number];

/**
 * A lucide icon drawn from the cached sprite (`/icons.svg`). Cards repeat the same icons many times; inline
 * lucide SVGs put every path into both the HTML and the RSC payload of each card, a `<use>` reference doesn't.
 * Same stroke attributes as lucide-react, so it looks identical.
 */
export function SpriteIcon({ name, className }: { name: SpriteIconName; className?: string }) {
  return (
    <svg
      aria-hidden="true"
      width="24"
      height="24"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={cn("shrink-0", className)}
    >
      <use href={`/icons.svg#${name}`} />
    </svg>
  );
}
