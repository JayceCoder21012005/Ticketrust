import type { Category } from "./types";

/**
 * Same list as the api's `EventCategories` (spec 04 `/categories`), kept in the bundle so pages that show
 * category chips stay prerenderable without calling the api at build time. A test keeps both lists in sync.
 */
export const CATEGORIES: readonly Category[] = [
  { slug: "concert", name: "Nhạc sống" },
  { slug: "festival", name: "Lễ hội" },
  { slug: "theater", name: "Sân khấu" },
  { slug: "sports", name: "Thể thao" },
  { slug: "comedy", name: "Hài kịch" },
  { slug: "workshop", name: "Hội thảo" },
];

export function categoryName(slug: string): string {
  return CATEGORIES.find((category) => category.slug === slug)?.name ?? slug;
}

export function isCategory(slug: string | undefined): slug is string {
  return CATEGORIES.some((category) => category.slug === slug);
}
