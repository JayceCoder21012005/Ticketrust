import { Drama, Laugh, Music, PartyPopper, Presentation, Trophy, type LucideIcon } from "lucide-react";
import Link from "next/link";
import { CATEGORIES } from "@/lib/catalog/categories";
import { cn } from "@/lib/utils";

const ICONS: Record<string, LucideIcon> = {
  concert: Music,
  festival: PartyPopper,
  theater: Drama,
  sports: Trophy,
  comedy: Laugh,
  workshop: Presentation,
};

type CategoryChipsProps = {
  /** Builds the link for a category; defaults to the listing filtered by it. */
  href?: (slug: string) => string;
  active?: string;
  className?: string;
};

/** Plain links (no client JS): scrolls sideways on phones, wraps on wider screens. */
export function CategoryChips({ href = (slug) => `/events?category=${slug}`, active, className }: CategoryChipsProps) {
  return (
    <ul className={cn("-mx-4 flex snap-x gap-2 overflow-x-auto px-4 pb-1 sm:mx-0 sm:flex-wrap sm:overflow-visible sm:px-0", className)}>
      {CATEGORIES.map(({ slug, name }) => {
        const Icon = ICONS[slug] ?? Music;
        const isActive = active === slug;
        return (
          <li key={slug} className="snap-start">
            <Link
              href={href(slug)}
              prefetch={false}
              aria-current={isActive ? "page" : undefined}
              className={cn(
                "flex h-10 items-center gap-2 whitespace-nowrap rounded-full border px-4 text-sm font-medium transition-colors hover:bg-muted",
                isActive && "border-primary bg-primary text-primary-foreground hover:bg-primary/90",
              )}
            >
              <Icon aria-hidden className="size-4" />
              {name}
            </Link>
          </li>
        );
      })}
    </ul>
  );
}
