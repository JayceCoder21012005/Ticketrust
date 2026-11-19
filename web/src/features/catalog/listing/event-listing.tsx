import { AlertTriangle, SearchX } from "lucide-react";
import Link from "next/link";
import { connection } from "next/server";
import { EmptyState } from "@/components/shared/empty-state";
import { EventCard, posterName } from "@/components/shared/event-card";
import { Button } from "@/components/ui/button";
import { getCities, getEventPage, getServerNow } from "@/lib/catalog/api";
import { isCategory } from "@/lib/catalog/categories";
import { activeFilterCount, listingHref, parseFilters, toApiQuery, type EventFilters } from "@/lib/catalog/filters";
import type { EventPage } from "@/lib/catalog/types";
import { t } from "@/lib/i18n.server";
import { ActiveFilters } from "./active-filters";
import { DesktopOnly } from "./desktop-only";
import { FilterDialog } from "./filter-dialog";
import { FilterPanel } from "./filter-panel";
import { LoadMore } from "./load-more";
import { SearchBox } from "./search-box";

/** 3 × 3 on desktop; more come from "Xem thêm". */
const PAGE_SIZE = "9";

type EventListingProps = { searchParams: Promise<Record<string, string | string[] | undefined>> };

export async function EventListing({ searchParams }: EventListingProps) {
  await connection();
  const parsed = parseFilters(await searchParams);
  // A stale or hand-edited category would be a 400 from the api; treat it as "no category" instead.
  const filters: EventFilters = { ...parsed, category: isCategory(parsed.category) ? parsed.category : undefined };

  const [cities, now] = await Promise.all([getCities().catch(() => []), getServerNow()]);
  const query = toApiQuery(filters, now);

  let page: EventPage | null = null;
  try {
    page = await getEventPage({ ...query, limit: PAGE_SIZE });
  } catch {
    page = null;
  }

  const count = activeFilterCount(filters);

  return (
    <div className="flex flex-col gap-6">
      <SearchBox filters={filters} />
      <div className="grid gap-8 lg:grid-cols-[16rem_minmax(0,1fr)]">
        <aside aria-label={t("catalog.listing.filters")} className="hidden lg:block">
          <DesktopOnly>
            <FilterPanel filters={filters} cities={cities} namePrefix="sidebar" className="sticky top-24" />
          </DesktopOnly>
        </aside>
        <section aria-label={t("nav.events")} className="flex flex-col gap-4">
          <div className="flex flex-wrap items-center gap-3">
            <div className="lg:hidden">
              <FilterDialog activeCount={count}>
                <FilterPanel filters={filters} cities={cities} namePrefix="dialog" />
              </FilterDialog>
            </div>
            {page ? (
              <p role="status" className="text-sm text-muted-foreground">
                {page.nextCursor
                  ? t("catalog.listing.resultsMore", "vi", { count: page.items.length })
                  : t("catalog.listing.results", "vi", { count: page.items.length })}
              </p>
            ) : null}
          </div>
          <ActiveFilters filters={filters} />
          {page === null ? (
            <EmptyState icon={AlertTriangle} title={t("states.errorTitle")} description={t("states.errorDescription")} />
          ) : page.items.length === 0 ? (
            <EmptyState
              icon={SearchX}
              title={t("catalog.listing.noResults")}
              description={t("catalog.listing.noResultsHint")}
              action={
                count > 0 || filters.q ? (
                  <Button asChild variant="outline">
                    <Link href={listingHref({})}>{t("catalog.listing.clearAll")}</Link>
                  </Button>
                ) : null
              }
            />
          ) : (
            <ul className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
              {page.items.map((event, i) => (
                <li key={event.id}>
                  <EventCard event={event} headingLevel="h2" priority={i === 0} posterTransitionName={posterName(event.slug)} className="h-full" />
                </li>
              ))}
              {page.nextCursor ? <LoadMore key={JSON.stringify(query)} query={query} cursor={page.nextCursor} /> : null}
            </ul>
          )}
        </section>
      </div>
    </div>
  );
}
