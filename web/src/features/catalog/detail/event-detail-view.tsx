import { CalendarPlus, ChevronDown, ExternalLink, MapPin, Ticket, Users } from "lucide-react";
import Link from "next/link";
import { EventPoster } from "@/components/catalog/event-poster";
import { Price } from "@/components/shared/price";
import { StickyActionBar } from "@/components/shared/sticky-action-bar";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { categoryName } from "@/lib/catalog/categories";
import { toParagraphs } from "@/lib/catalog/paragraphs";
import { externalHttpUrl } from "@/lib/catalog/safe-url";
import type { EventDetail } from "@/lib/catalog/types";
import { calendarTile, formatDateTime, formatEventDate, formatTimeRange } from "@/lib/format";
import { t } from "@/lib/i18n.server";
import { EventCta } from "../event-cta";

const FAQ = [1, 2, 3, 4] as const;

/** Luma-style event page (spec 04): poster left, facts and the buy card right; details below. */
export function EventDetailView({ event }: { event: EventDetail }) {
  const priceFrom = event.tiers.length > 0 ? Math.min(...event.tiers.map((tier) => tier.price)) : null;
  const tile = calendarTile(event.startsAt);
  const mapUrl = externalHttpUrl(event.venue.mapUrl);

  return (
    <article className="mx-auto flex w-full max-w-content flex-col gap-10 px-4 pb-32 pt-6 md:pb-16 md:pt-10">
      <div className="grid gap-8 md:grid-cols-[minmax(0,5fr)_minmax(0,6fr)] lg:gap-12">
        <div className="relative aspect-square overflow-hidden rounded-2xl border bg-muted md:sticky md:top-24 md:self-start">
          <EventPoster
            slug={event.slug}
            category={event.category}
            title={event.title}
            coverImageUrl={event.coverImageUrl}
            priority
            size="hero"
            sizes="(min-width: 768px) 45vw, 100vw"
          />
        </div>

        <div className="flex flex-col gap-6">
          <div className="flex flex-col gap-3">
            <div className="flex flex-wrap items-center gap-2">
              <Link
                href={`/events?category=${event.category}`}
                prefetch={false}
                className="text-sm font-semibold uppercase tracking-widest text-primary hover:underline"
              >
                {categoryName(event.category)}
              </Link>
              {event.status === "Ended" ? <Badge variant="secondary">{t("catalog.cta.ended")}</Badge> : null}
            </div>
            <h1 className="text-3xl font-bold leading-tight tracking-tight text-balance sm:text-4xl">{event.title}</h1>
          </div>

          <div className="flex flex-col gap-4">
            <div className="flex items-start gap-4">
              <div aria-hidden className="flex size-12 shrink-0 flex-col overflow-hidden rounded-lg border text-center">
                <span className="bg-muted text-[0.65rem] font-semibold uppercase leading-4 text-muted-foreground">{tile.month}</span>
                <span className="flex flex-1 items-center justify-center text-lg font-bold leading-none">{tile.day}</span>
              </div>
              <div className="flex min-w-0 flex-1 flex-col">
                <p className="font-semibold first-letter:uppercase">{formatEventDate(event.startsAt)}</p>
                <p className="text-sm text-muted-foreground">{formatTimeRange(event.startsAt, event.endsAt)}</p>
                <p>
                  <a
                    href={`/events/${event.slug}/calendar.ics`}
                    download={`${event.slug}.ics`}
                    className="mt-1 inline-flex items-center gap-1.5 text-sm font-medium text-primary hover:underline"
                  >
                    <CalendarPlus aria-hidden className="size-4" />
                    {t("catalog.detail.addToCalendar")}
                  </a>
                </p>
              </div>
            </div>

            <div className="flex items-start gap-4">
              <div aria-hidden className="flex size-12 shrink-0 items-center justify-center rounded-lg border">
                <MapPin className="size-5 text-muted-foreground" />
              </div>
              <div className="flex min-w-0 flex-1 flex-col">
                <p className="font-semibold">{event.venue.name}</p>
                <p className="text-sm text-muted-foreground">
                  {event.venue.address}, {event.venue.city}
                </p>
                {mapUrl ? (
                  <p>
                    <a
                      href={mapUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="mt-1 inline-flex items-center gap-1.5 text-sm font-medium text-primary hover:underline"
                    >
                      {t("catalog.detail.openMap")}
                      <ExternalLink aria-hidden className="size-3.5" />
                    </a>
                  </p>
                ) : null}
              </div>
            </div>
          </div>

          <div className="hidden md:block">
            <EventCta event={event} priceFrom={priceFrom} />
          </div>
        </div>
      </div>

      <div className="flex max-w-3xl flex-col gap-10">
        {event.lineup.length > 0 ? (
          <Section id="lineup" title={t("catalog.detail.lineup")}>
            <ul className="flex flex-wrap gap-3">
              {event.lineup.map((artist) => (
                <li key={artist} className="flex items-center gap-2 rounded-full border py-1 pl-1 pr-4">
                  <span aria-hidden className="flex size-8 items-center justify-center rounded-full bg-primary/15 text-sm font-semibold text-primary">
                    {artist.charAt(0)}
                  </span>
                  <span className="font-medium">{artist}</span>
                </li>
              ))}
            </ul>
          </Section>
        ) : null}

        {event.description ? (
          <Section id="about" title={t("catalog.detail.about")}>
            <div className="flex flex-col gap-3 leading-relaxed text-foreground/90">
              {/* Plain paragraphs: organizer text is never rendered as HTML. */}
              {toParagraphs(event.description).map((paragraph, i) => (
                <p key={i} className="whitespace-pre-line">
                  {paragraph}
                </p>
              ))}
            </div>
          </Section>
        ) : null}

        {event.tiers.length > 0 ? (
          <Section id="prices" title={t("catalog.detail.prices")}>
            <ul className="divide-y rounded-xl border">
              {event.tiers.map((tier) => (
                <li key={tier.id} className="flex items-center gap-3 px-4 py-3">
                  <span aria-hidden className="size-3 shrink-0 rounded-full" style={{ backgroundColor: tier.color }} />
                  <span className="flex min-w-0 flex-1 flex-col">
                    <span className="font-medium">{tier.name}</span>
                    <span className="flex items-center gap-1 text-xs text-muted-foreground">
                      {tier.kind === "Seated" ? <Ticket aria-hidden className="size-3" /> : <Users aria-hidden className="size-3" />}
                      {tier.kind === "Seated" ? t("catalog.detail.seated") : t("catalog.detail.standing")}
                    </span>
                  </span>
                  <Price amount={tier.price} />
                </li>
              ))}
            </ul>
            <p className="text-xs text-muted-foreground">{t("catalog.detail.allIn")}</p>
          </Section>
        ) : null}

        {event.phases.length > 0 ? (
          <Section id="phases" title={t("catalog.detail.phases")}>
            <ol className="flex flex-col gap-3">
              {event.phases.map((phase) => (
                <li key={phase.id} className="flex flex-col gap-2 rounded-xl border px-4 py-3">
                  <span className="font-medium">{phase.name}</span>
                  <span className="text-sm text-muted-foreground">
                    {formatDateTime(phase.opensAt)} → {formatDateTime(phase.closesAt)}
                  </span>
                  <span className="flex flex-wrap gap-2">
                    {phase.queueEnabled ? <Badge variant="outline">{t("catalog.detail.queue")}</Badge> : null}
                    <Badge variant="outline">{t("catalog.detail.maxPerUser", "vi", { count: phase.maxTicketsPerUser })}</Badge>
                    {phase.requiresAccessCode ? <Badge variant="outline">{t("catalog.detail.accessCode")}</Badge> : null}
                  </span>
                </li>
              ))}
            </ol>
          </Section>
        ) : null}

        <Section id="faq" title={t("catalog.detail.faq")}>
          <div className="divide-y rounded-xl border">
            {FAQ.map((n) => (
              // Native disclosure: accessible and zero JavaScript.
              <details key={n} className="group px-4 py-3 [&_summary::-webkit-details-marker]:hidden">
                <summary className="flex cursor-pointer list-none items-center justify-between gap-4 font-medium">
                  {t(`catalog.detail.faqQ${n}`)}
                  <ChevronDown aria-hidden className="size-4 shrink-0 transition-transform group-open:rotate-180" />
                </summary>
                <p className="pt-2 text-sm leading-relaxed text-muted-foreground">{t(`catalog.detail.faqA${n}`)}</p>
              </details>
            ))}
          </div>
        </Section>

        <Button asChild variant="link" className="self-start px-0">
          <Link href="/events" prefetch={false}>
            {t("catalog.detail.backToEvents")}
          </Link>
        </Button>
      </div>

      <StickyActionBar label={t("catalog.cta.label")}>
        <EventCta event={event} priceFrom={priceFrom} variant="bar" />
      </StickyActionBar>
    </article>
  );
}

function Section({ id, title, children }: { id: string; title: string; children: React.ReactNode }) {
  return (
    <section aria-labelledby={id} className="flex flex-col gap-4">
      <h2 id={id} className="text-xl font-bold tracking-tight">
        {title}
      </h2>
      {children}
    </section>
  );
}
