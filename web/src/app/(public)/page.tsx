import { Suspense } from "react";
import { CategoryChips } from "@/components/catalog/category-chips";
import { Skeleton } from "@/components/ui/skeleton";
import { HomeHero, HomeLists } from "@/features/catalog/home/home-sections";
import { t } from "@/lib/i18n.server";

/**
 * Home (spec 04): the next sale to open, categories, sales opening soon with live countdowns, cities and the
 * upcoming grid. An ISR page: static HTML from the cached catalog, regenerated in the background every 30 s.
 */
export default function Home() {
  return (
    <div className="mx-auto flex w-full max-w-content flex-col gap-10 px-4 py-6 sm:gap-12 sm:py-10">
      <h1 className="sr-only">{t("catalog.home.title")}</h1>
      <Suspense fallback={<Skeleton className="aspect-video w-full rounded-2xl md:aspect-[21/8]" />}>
        <HomeHero />
      </Suspense>
      <nav aria-labelledby="categories-title" className="flex flex-col gap-4">
        <h2 id="categories-title" className="text-xl font-bold tracking-tight sm:text-2xl">
          {t("catalog.home.categories")}
        </h2>
        <CategoryChips />
      </nav>
      <Suspense fallback={<GridSkeleton />}>
        <HomeLists />
      </Suspense>
    </div>
  );
}

function GridSkeleton() {
  return (
    <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
      {Array.from({ length: 6 }, (_, i) => (
        <Skeleton key={i} className="aspect-[4/3] rounded-xl" />
      ))}
    </div>
  );
}
