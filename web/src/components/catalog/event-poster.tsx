import { getImageProps } from "next/image";
import { categoryName } from "@/lib/catalog/categories";
import { posterTheme } from "@/lib/catalog/poster";
import { cn } from "@/lib/utils";

type EventPosterProps = {
  slug: string;
  category: string;
  title: string;
  coverImageUrl: string | null;
  sizes: string;
  /** Only the above-the-fold poster may be the LCP candidate. */
  priority?: boolean;
  size?: "card" | "hero";
  className?: string;
};

/**
 * Fills its (relative, fixed-ratio) parent with the cover image, or with a generated poster when there is none.
 * The generated one is decorative (the title is in the page next to it), so it is hidden from screen readers.
 */
export function EventPoster({ slug, category, title, coverImageUrl, sizes, priority = false, size = "card", className }: EventPosterProps) {
  if (coverImageUrl) {
    // getImageProps gives the optimised srcset without the client <Image> component (~7 KB of JS per page).
    const { props } = getImageProps({ src: coverImageUrl, alt: "", fill: true, priority, sizes });
    // eslint-disable-next-line @next/next/no-img-element -- props come from getImageProps (optimised srcset)
    return <img {...props} alt="" className={cn("object-cover", className)} />;
  }

  const theme = posterTheme(slug, category);

  return (
    <div
      aria-hidden
      className={cn("absolute inset-0 flex flex-col justify-end gap-1 overflow-hidden text-white", size === "hero" ? "p-6 sm:p-8" : "p-4", className)}
      style={{
        backgroundImage: [
          "radial-gradient(circle at 85% 15%, oklch(1 0 0 / 0.28), transparent 45%)",
          "radial-gradient(circle at 10% 110%, oklch(0 0 0 / 0.35), transparent 55%)",
          `linear-gradient(${theme.angle}deg, ${theme.from}, ${theme.to})`,
        ].join(", "),
      }}
    >
      <span className="text-xs font-semibold uppercase tracking-widest opacity-85">{categoryName(category)}</span>
      <span className={cn("line-clamp-3 font-bold leading-tight text-balance", size === "hero" ? "text-3xl sm:text-4xl" : "text-xl")}>
        {title}
      </span>
    </div>
  );
}
