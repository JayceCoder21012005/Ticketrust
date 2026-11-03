import { AlertTriangle, ArrowRight, CalendarDays, MapPin } from "lucide-react";
import Link from "next/link";
import { EventPoster } from "@/components/catalog/event-poster";
import { SaleCountdown } from "@/components/catalog/sale-countdown";
import { EmptyState } from "@/components/shared/empty-state";
import { EventCard } from "@/components/shared/event-card";
import { Price } from "@/components/shared/price";
import { Button } from "@/components/ui/button";
import { getCities, getEventPage } from "@/lib/catalog/api";
import type { EventSummary } from "@/lib/catalog/types";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n.server";

const OPENING_SOON = 4;
const UPCOMING = 6;

/**
 * One cached api call feeds the whole home page; the catalog is small enough to pick sections from it.
 * No request-time APIs: the home page is an ISR page (static HTML, refreshed every 30 s).
 */
async function loadHomeEvents(): Promise<EventSummary[] | null> {
  try {
    return (await getEventPage({ limit: "50" })).items;
  } catch {
    return null;
  }
}

/** Next sale to open (the moment a flash-sale site is about), else something on sale now. */
export function pickFeatured(events: EventSummary[]): EventSummary | undefined {
  const opening = openingSoon(events);
  return opening[0] ?? events.find((e) => e.isOnSale) ?? events[0];
}

export function openingSoon(events: EventSummary[]): EventSummary[] {
  return events
    .filter((e) => e.nextSaleOpensAt && !e.isOnSale && !e.isSoldOut)
    .sort((a, b) => Date.parse(a.nextSaleOpensAt!) - Date.parse(b.nextSaleOpensAt!));
}

export async function HomeHero() {
  const events = await loadHomeEvents();
  if (events === null) {
    return <EmptyState icon={AlertTriangle} title={t("states.errorTitle")} description={t("states.errorDescription")} />;
  }

  const featured = pickFeatured(events);
  if (!featured) {
    return <EmptyState title={t("catalog.home.empty")} description={t("catalog.home.emptyDescription")} />;
  }

  const upcoming = !featured.isOnSale && featured.nextSaleOpensAt;

  return (
    <section aria-labelledby="hero-title" className="grid overflow-hidden rounded-2xl border bg-card md:grid-cols-[1.25fr_1fr]">
      <Link href={`/events/${featured.slug}`} prefetch={false} tabIndex={-1} aria-hidden className="relative block aspect-video md:aspect-auto md:min-h-80">
        <EventPoster
          slug={featured.slug}
          category={featured.category}
          title={featured.title}
          coverImageUrl={featured.coverImageUrl}
          priority
          size="hero"
          sizes="(min-width: 768px) 60vw, 100vw"
        />
      </Link>
      <div className="flex flex-col gap-4 p-6 sm:p-8">
        <p className="text-sm font-semibold uppercase tracking-widest text-primary">
          {upcoming ? t("catalog.home.heroSoon") : t("catalog.home.heroOnSale")}
        </p>
        <h2 id="hero-title" className="text-2xl font-bold leading-tight tracking-tight text-balance sm:text-3xl">
          <Link href={`/events/${featured.slug}`} prefetch={false} className="hover:underline">
            {featured.title}
          </Link>
        </h2>
        <div className="flex flex-col gap-1.5 text-sm text-muted-foreground">
          <p className="flex items-center gap-2">
            <CalendarDays aria-hidden className="size-4" />
            {formatDateTime(featured.startsAt)}
          </p>
          <p className="flex items-center gap-2">
            <MapPin aria-hidden className="size-4" />
            {featured.venueName}, {featured.city}
          </p>
        </div>
        {upcoming ? (
          <div className="rounded-xl bg-muted px-4 py-3">
            <p className="text-sm text-muted-foreground">{t("catalog.cta.opensIn")}</p>
            <SaleCountdown opensAt={featured.nextSaleOpensAt!} className="text-2xl" />
          </div>
        ) : null}
        <div className="mt-auto flex flex-wrap items-center justify-between gap-3 pt-2">
          {featured.priceFrom !== null ? <Price amount={featured.priceFrom} from className="text-lg" /> : <span />}
          <Button asChild size="lg">
            <Link href={`/events/${featured.slug}`} prefetch={false}>
              {upcoming ? t("catalog.cta.label") : t("catalog.cta.buy")}
              <ArrowRight aria-hidden />
            </Link>
          </Button>
        </div>
      </div>
    </section>
  );
}

export async function HomeLists() {
  const [events, cities] = await Promise.all([loadHomeEvents(), getCities().catch(() => [])]);
  if (!events || events.length === 0) {
    return null;
  }

  const soon = openingSoon(events).slice(0, OPENING_SOON);

  return (
    <>
      {soon.length > 0 ? (
        <section aria-labelledby="opening-soon" className="flex flex-col gap-4">
          <SectionHeading id="opening-soon" title={t("catalog.home.openingSoon")} description={t("catalog.home.openingSoonDescription")} />
          <ul className="-mx-4 flex snap-x snap-mandatory gap-4 overflow-x-auto px-4 pb-2 sm:mx-0 sm:grid sm:grid-cols-2 sm:overflow-visible sm:px-0 lg:grid-cols-4">
            {soon.map((event) => (
              <li key={event.id} className="flex w-[78%] shrink-0 snap-start flex-col gap-2 sm:w-auto">
                <EventCard event={event} className="flex-1" />
                <p className="px-1 text-sm text-muted-foreground">
                  {t("catalog.cta.opensIn")} <SaleCountdown opensAt={event.nextSaleOpensAt!} className="text-foreground" />
                </p>
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {cities.length > 0 ? (
        <section aria-labelledby="by-city" className="flex flex-col gap-4">
          <SectionHeading id="by-city" title={t("catalog.home.byCity")} />
          <ul className="flex flex-wrap gap-2">
            {cities.map((city) => (
              <li key={city}>
                <Link
                  href={`/events?city=${encodeURIComponent(city)}`}
                  prefetch={false}
                  className="flex h-10 items-center gap-2 rounded-full border px-4 text-sm font-medium hover:bg-muted"
                >
                  <MapPin aria-hidden className="size-4 text-primary" />
                  {city}
                </Link>
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      <section aria-labelledby="upcoming" className="flex flex-col gap-4">
        <SectionHeading
          id="upcoming"
          title={t("catalog.home.upcoming")}
          action={
            <Link href="/events" prefetch={false} className="flex items-center gap-1 text-sm font-medium text-primary hover:underline">
              {t("catalog.home.viewAll")}
              <ArrowRight aria-hidden className="size-4" />
            </Link>
          }
        />
        <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {events.slice(0, UPCOMING).map((event) => (
            <li key={event.id}>
              <EventCard event={event} className="h-full" />
            </li>
          ))}
        </ul>
      </section>
    </>
  );
}

function SectionHeading({ id, title, description, action }: { id: string; title: string; description?: string; action?: React.ReactNode }) {
  return (
    <div className="flex items-end justify-between gap-4">
      <div className="flex flex-col gap-1">
        <h2 id={id} className="text-xl font-bold tracking-tight sm:text-2xl">
          {title}
        </h2>
        {description ? <p className="text-sm text-muted-foreground">{description}</p> : null}
      </div>
      {action}
    </div>
  );
}
