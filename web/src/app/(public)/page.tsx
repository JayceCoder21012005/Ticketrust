import { Suspense } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { HomeSectionsLoader } from "@/features/catalog/home/home-sections";

/**
 * Home (UI refresh): a photo hero with the ⌘K search, then hot, opening soon, this week, cities, categories, upcoming and "for
 * you". An ISR page: static HTML from the cached `/catalog/home`, regenerated in the background every 30 s.
 */
export default function Home() {
  return (
    <div className="mx-auto flex w-full max-w-content flex-col gap-10 px-4 py-6 sm:gap-14 sm:py-8">
      <Suspense fallback={<HomeSkeleton />}>
        <HomeSectionsLoader />
      </Suspense>
    </div>
  );
}

function HomeSkeleton() {
  return (
    <>
      <Skeleton className="min-h-[22rem] w-full rounded-3xl sm:min-h-[26rem]" />
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {Array.from({ length: 6 }, (_, i) => (
          <Skeleton key={i} className="aspect-[4/3] rounded-xl" />
        ))}
      </div>
    </>
  );
}
