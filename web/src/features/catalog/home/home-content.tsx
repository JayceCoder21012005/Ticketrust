import { ArrowRight, Drama, Laugh, MapPin, Music, PartyPopper, Presentation, Trophy, type LucideIcon } from "lucide-react";
import { getImageProps } from "next/image";
import Link from "next/link";
import type { ReactNode } from "react";
import { SaleCountdown } from "@/components/catalog/sale-countdown";
import { EmptyState } from "@/components/shared/empty-state";
import { EventCard } from "@/components/shared/event-card";
import { SearchTrigger } from "@/features/search/search-trigger";
import { categoryName } from "@/lib/catalog/categories";
import type { EventSummary, HomeSections } from "@/lib/catalog/types";
import { t } from "@/lib/i18n.server";
import { cn } from "@/lib/utils";
import { ForYou } from "./for-you";
import { bandPhotoFor, heroPhotoFor } from "./photos";

const CATEGORY_ICONS: Record<string, LucideIcon> = {
  concert: Music,
  festival: PartyPopper,
  theater: Drama,
  sports: Trophy,
  comedy: Laugh,
  workshop: Presentation,
};

/**
 * The discovery home (UI refresh): a photo hero with the search, then sections picked on the server in a fixed order (hot,
 * opening soon, this week, cities, categories, upcoming) and "for you" for a signed-in buyer. A section without events is
 * not rendered at all. Synchronous and data-in, so the page stays static (ISR) and this stays testable.
 */
export function HomeContent({ home, now }: { home: HomeSections; now: Date }) {
  const nothing = home.hot.length + home.openingSoon.length + home.thisWeek.length + home.upcoming.length === 0;
  const categories = home.categories.filter((c) => c.events > 0);

  return (
    <>
      <Hero now={now} />
      {nothing ? (
        <EmptyState title={t("catalog.home.empty")} description={t("catalog.home.emptyDescription")} />
      ) : (
        <>
          {home.hot.length > 0 ? (
            <Section id="hot" title={t("catalog.home.hot")} description={t("catalog.home.hotDescription")} backdrop={bandPhotoFor(now)}>
              <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4 lg:grid-rows-2">
                {home.hot.map((event, i) => (
                  <li key={event.id} className={cn(i === 0 && "sm:col-span-2 lg:row-span-2")}>
                    <EventCard event={{ ...event, isHot: true }} variant={i === 0 ? "large" : "default"} priority={i === 0} className="h-full" />
                  </li>
                ))}
              </ul>
            </Section>
          ) : null}

          {home.openingSoon.length > 0 ? (
            <Section id="opening-soon" title={t("catalog.home.openingSoon")} description={t("catalog.home.openingSoonDescription")}>
              <ul className="-mx-4 flex snap-x snap-mandatory gap-4 overflow-x-auto px-4 pb-2 sm:mx-0 sm:grid sm:grid-cols-2 sm:overflow-visible sm:px-0 lg:grid-cols-4">
                {home.openingSoon.map((event) => (
                  <li key={event.id} className="flex w-[78%] shrink-0 snap-start flex-col gap-2 sm:w-auto">
                    <EventCard event={event} variant="compact" className="flex-1" />
                    <p className="px-1 text-sm text-muted-foreground">
                      {t("catalog.cta.opensIn")} <SaleCountdown opensAt={event.nextSaleOpensAt!} className="font-semibold text-live" />
                    </p>
                  </li>
                ))}
              </ul>
            </Section>
          ) : null}

          {home.thisWeek.length > 0 ? <CardGrid id="this-week" title={t("catalog.home.thisWeek")} events={home.thisWeek} /> : null}

          {home.cities.length > 0 ? (
            <Section id="by-city" title={t("catalog.home.byCity")}>
              <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
                {home.cities.map(({ city, events }, i) => (
                  <li key={city}>
                    <Link
                      href={`/events?${new URLSearchParams({ city })}`}
                      prefetch={false}
                      // A brand gradient turned by the city's position: each tile its own hue, all of them on brand.
                      style={{ filter: `hue-rotate(${(i * 47) % 360}deg)` }}
                      className="group/city relative flex h-28 flex-col justify-end overflow-hidden rounded-2xl bg-brand-gradient p-4 text-white shadow-sm outline-none transition-transform focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 motion-safe:hover:-translate-y-0.5"
                    >
                      <MapPin aria-hidden className="absolute right-3 top-3 size-5 opacity-70" />
                      <span className="text-lg font-bold leading-tight">{city}</span>
                      <span className="text-sm text-white/85">{t("catalog.home.eventsCount", "vi", { count: events })}</span>
                    </Link>
                  </li>
                ))}
              </ul>
            </Section>
          ) : null}

          {categories.length > 0 ? (
            <nav aria-labelledby="categories-title" className="flex flex-col gap-4">
              <SectionHeading id="categories-title" title={t("catalog.home.categories")} />
              <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
                {categories.map(({ category, events }) => {
                  const Icon = CATEGORY_ICONS[category] ?? Music;
                  return (
                    <li key={category}>
                      <Link
                        href={`/events?category=${category}`}
                        prefetch={false}
                        className="flex h-full flex-col gap-2 rounded-2xl border bg-card p-4 outline-none transition-colors hover:border-primary/40 hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring"
                      >
                        <Icon aria-hidden className="size-6 text-primary" />
                        <span className="font-semibold">{categoryName(category)}</span>
                        <span className="text-sm text-muted-foreground">{t("catalog.home.eventsCount", "vi", { count: events })}</span>
                      </Link>
                    </li>
                  );
                })}
              </ul>
            </nav>
          ) : null}

          {home.upcoming.length > 0 ? (
            <CardGrid
              id="upcoming"
              title={t("catalog.home.upcoming")}
              events={home.upcoming}
              action={
                <Link href="/events" prefetch={false} className="flex items-center gap-1 text-sm font-medium text-primary hover:underline">
                  {t("catalog.home.viewAll")}
                  <ArrowRight aria-hidden className="size-4" />
                </Link>
              }
            />
          ) : null}

          <ForYou />
        </>
      )}
    </>
  );
}

function Hero({ now }: { now: Date }) {
  // getImageProps gives the optimised srcset without the client <Image> component (~7 KB of JS per page).
  const { props } = getImageProps({ src: heroPhotoFor(now), alt: "", fill: true, priority: true, sizes: "(min-width: 1200px) 1200px, 100vw" });
  return (
    <section aria-labelledby="hero-title" className="relative isolate overflow-hidden rounded-3xl">
      {/* eslint-disable-next-line @next/next/no-img-element -- props come from getImageProps (optimised srcset) */}
      <img {...props} alt="" className="-z-20 object-cover" />
      {/* The veil keeps white text above 4.5:1 whatever the photo. */}
      <div aria-hidden className="absolute inset-0 -z-10 bg-gradient-to-t from-black/85 via-black/55 to-black/35" />
      <div className="flex min-h-[22rem] flex-col items-center justify-center gap-5 px-4 py-14 text-center text-white sm:min-h-[26rem]">
        <h1 id="hero-title" className="text-balance text-4xl font-extrabold tracking-tight sm:text-5xl lg:text-6xl">
          {t("catalog.home.heroTitle")}
        </h1>
        <p className="max-w-xl text-balance text-base text-white/85 sm:text-lg">{t("catalog.home.heroSubtitle")}</p>
        <SearchTrigger variant="hero" className="text-foreground" />
      </div>
    </section>
  );
}

function Section({ id, title, description, backdrop, children }: { id: string; title: string; description?: string; backdrop?: string; children: ReactNode }) {
  const image = backdrop ? getImageProps({ src: backdrop, alt: "", fill: true, sizes: "100vw" }).props : null;
  return (
    <section aria-labelledby={id} className={cn("reveal flex flex-col gap-4", image && "relative isolate -mx-4 overflow-hidden px-4 py-6 sm:mx-0 sm:rounded-3xl sm:px-6")}>
      {image ? (
        <>
          {/* eslint-disable-next-line @next/next/no-img-element -- props come from getImageProps (optimised srcset) */}
          <img {...image} alt="" className="-z-20 scale-110 object-cover opacity-25 blur-2xl" />
          <div aria-hidden className="absolute inset-0 -z-10 bg-background/70" />
        </>
      ) : null}
      <SectionHeading id={id} title={title} description={description} />
      {children}
    </section>
  );
}

function CardGrid({ id, title, events, action }: { id: string; title: string; events: EventSummary[]; action?: ReactNode }) {
  return (
    <section aria-labelledby={id} className="reveal flex flex-col gap-4">
      <SectionHeading id={id} title={title} action={action} />
      <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {events.map((event) => (
          <li key={event.id}>
            <EventCard event={event} className="h-full" />
          </li>
        ))}
      </ul>
    </section>
  );
}

function SectionHeading({ id, title, description, action }: { id: string; title: string; description?: string; action?: ReactNode }) {
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
