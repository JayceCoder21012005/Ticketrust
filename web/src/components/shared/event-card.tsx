import Link from "next/link";
import { EventPoster } from "@/components/catalog/event-poster";
import { SaleBadge } from "@/components/catalog/sale-badge";
import { formatDateTime } from "@/lib/format";
import { cn } from "@/lib/utils";
import { Price } from "./price";
import { SpriteIcon } from "./sprite-icon";

export type EventCardData = {
  slug: string;
  title: string;
  startsAt: string;
  venueName: string;
  city: string;
  coverImageUrl: string | null;
  priceFrom: number | null;
  category?: string;
  isOnSale?: boolean;
  isSoldOut?: boolean;
  nextSaleOpensAt?: string | null;
};

type EventCardProps = {
  event: EventCardData;
  /** Only the first, above-the-fold card should be the LCP candidate. */
  priority?: boolean;
  /** Heading level of the title inside the surrounding page outline. */
  headingLevel?: "h2" | "h3";
  className?: string;
};

export function EventCard({ event, priority = false, headingLevel: Heading = "h3", className }: EventCardProps) {
  return (
    <Link
      href={`/events/${event.slug}`}
      // Prefetching would download the event page's JS on every catalog page (counted in the 170 KB budget).
      prefetch={false}
      className={cn(
        "group flex flex-col overflow-hidden rounded-xl border bg-card transition-shadow hover:shadow-md focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring",
        className,
      )}
    >
      {/* Fixed 16:9 box reserves space so the grid never shifts (CLS). */}
      <div className="relative aspect-video overflow-hidden bg-muted">
        <EventPoster
          slug={event.slug}
          category={event.category ?? ""}
          title={event.title}
          coverImageUrl={event.coverImageUrl}
          priority={priority}
          sizes="(min-width: 1024px) 380px, (min-width: 640px) 50vw, 100vw"
          className="transition-transform duration-200 group-hover:scale-[1.02]"
        />
        <SaleBadge
          event={{ isOnSale: event.isOnSale ?? false, isSoldOut: event.isSoldOut ?? false, nextSaleOpensAt: event.nextSaleOpensAt ?? null }}
          className="absolute left-3 top-3 shadow-sm"
        />
      </div>
      <div className="flex flex-1 flex-col gap-2 p-4">
        <Heading className="line-clamp-2 font-semibold leading-snug">{event.title}</Heading>
        {/* Sprite icons: inline lucide SVGs would repeat every path in the HTML and RSC payload of each card. */}
        <p className="flex items-center gap-1.5 text-sm text-muted-foreground">
          <SpriteIcon name="calendar-days" className="size-4" />
          {formatDateTime(event.startsAt)}
        </p>
        <p className="flex items-center gap-1.5 text-sm text-muted-foreground">
          <SpriteIcon name="map-pin" className="size-4" />
          <span className="line-clamp-1">
            {event.venueName}, {event.city}
          </span>
        </p>
        {event.priceFrom !== null ? <Price amount={event.priceFrom} from className="mt-auto pt-2 text-primary" /> : null}
      </div>
    </Link>
  );
}
