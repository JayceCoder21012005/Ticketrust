import { PageTransition } from "@/components/shared/event-card";
import { HomeHeroLoader, HomeSectionsLoader } from "@/features/catalog/home/home-sections";

/**
 * Home (UI refresh): a photo hero with the ⌘K search, then hot, opening soon, this week, cities, categories, upcoming and "for
 * you". An ISR page: static HTML from the cached `/catalog/home`, regenerated in the background every 30 s. No Suspense
 * boundary: every piece reads cached data, so the whole page is in the static shell; streaming it behind a boundary cost a
 * hidden-div swap script and a full re-layout on load (measured as total blocking time).
 */
export default function Home() {
  return (
    <PageTransition>
      <div className="mx-auto flex w-full max-w-content flex-col gap-10 px-4 py-6 sm:gap-14 sm:py-8">
        <HomeHeroLoader />
        <HomeSectionsLoader />
      </div>
    </PageTransition>
  );
}
