/**
 * The owner's Unsplash photos (UI refresh), prepared by scripts/prepare-photos.mjs. They are mood backdrops only and are never
 * labelled as a city: they show places abroad, and the catalog's cities are Vietnamese.
 */
export const PHOTOS = [
  "/photos/cinque-terre.jpg",
  "/photos/venice.jpg",
  "/photos/bukchon.jpg",
  "/photos/bavaria.jpg",
  "/photos/main-street.jpg",
] as const;

export type Photo = (typeof PHOTOS)[number];

const DAY_MS = 24 * 60 * 60 * 1000;
const VIETNAM_OFFSET_MS = 7 * 60 * 60 * 1000;

/** The hero backdrop of a (Vietnam) calendar day: one photo all day, the next one tomorrow. */
export function heroPhotoFor(date: Date): Photo {
  const day = Math.floor((date.getTime() + VIETNAM_OFFSET_MS) / DAY_MS);
  return PHOTOS[day % PHOTOS.length];
}
