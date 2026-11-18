import Link from "next/link";
import { EventPoster } from "@/components/catalog/event-poster";
import { SaleBadge } from "@/components/catalog/sale-badge";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardFooter } from "@/components/ui/card";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n.server";
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
  /** In the home's "hot" section (UI refresh): the chip says so. */
  isHot?: boolean;
};

export type EventCardVariant = "default" | "large" | "compact";

type EventCardProps = {
  event: EventCardData;
  /** `large` leads the hot bento, `compact` fits horizontal rows and the account hub. Same anatomy everywhere. */
  variant?: EventCardVariant;
  /** Only the first, above-the-fold card should be the LCP candidate. */
  priority?: boolean;
  /** Heading level of the title inside the surrounding page outline. */
  headingLevel?: "h2" | "h3";
  /** Names the poster for the shared-element morph into the event page (UI refresh, motion). */
  posterTransitionName?: string;
  className?: string;
};

const SIZES: Record<EventCardVariant, string> = {
  default: "(min-width: 1024px) 380px, (min-width: 640px) 50vw, 100vw",
  large: "(min-width: 1024px) 600px, 100vw",
  compact: "(min-width: 640px) 280px, 80vw",
};

/**
 * The one event card (UI refresh): the whole card is the link (NN/g), the status chip on the poster, then title, date, place,
 * and the price always in the last row. Hover lifts it and zooms the poster, a press sinks it, and a soft spotlight follows the
 * pointer along the border (`[data-spotlight]`, see card-spotlight.ts). All motion is `motion-safe` only.
 */
export function EventCard({
  event,
  variant = "default",
  priority = false,
  headingLevel: Heading = "h3",
  posterTransitionName,
  className,
}: EventCardProps) {
  const soldOut = event.isSoldOut ?? false;
  return (
    <Link
      href={`/events/${event.slug}`}
      // Prefetching would download the event page's JS on every catalog page (counted in the 170 KB budget).
      prefetch={false}
      data-variant={variant}
      data-spotlight=""
      data-sold-out={soldOut ? "" : undefined}
      className={cn(
        "group/event relative block rounded-xl outline-none",
        "before:pointer-events-none before:absolute before:-inset-px before:z-10 before:rounded-[inherit] before:opacity-0 before:transition-opacity before:duration-200",
        "before:[background:radial-gradient(260px_circle_at_var(--x,50%)_var(--y,0%),color-mix(in_oklch,var(--primary)_55%,transparent),transparent_70%)] before:[mask:linear-gradient(#000_0_0)_content-box_exclude,linear-gradient(#000_0_0)] before:p-px",
        "hover:before:opacity-100 focus-visible:before:opacity-100",
        "focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-background",
        "motion-safe:transition-transform motion-safe:duration-200 motion-safe:ease-out motion-safe:hover:-translate-y-0.5 motion-safe:active:scale-[0.98] motion-safe:active:duration-100",
        soldOut && "grayscale-[0.7]",
        className,
      )}
    >
      <Card className="h-full gap-0 py-0 transition-shadow duration-200 group-hover/event:shadow-lg">
        {/* Fixed ratio box reserves space so the grid never shifts (CLS). */}
        <div
          data-poster=""
          style={posterTransitionName ? { viewTransitionName: posterTransitionName } : undefined}
          className={cn("relative overflow-hidden bg-muted", variant === "compact" ? "aspect-[16/10]" : "aspect-video")}
        >
          <EventPoster
            slug={event.slug}
            category={event.category ?? ""}
            title={event.title}
            coverImageUrl={event.coverImageUrl}
            priority={priority && variant !== "compact"}
            sizes={SIZES[variant]}
            className="motion-safe:transition-transform motion-safe:duration-300 motion-safe:ease-out motion-safe:group-hover/event:scale-[1.03]"
          />
          <div className="absolute left-3 top-3 flex flex-wrap gap-1.5">
            {event.isHot && !soldOut ? (
              <Badge className="bg-live text-live-foreground shadow-sm">{t("catalog.card.hot")}</Badge>
            ) : null}
            <SaleBadge
              event={{ isOnSale: event.isOnSale ?? false, isSoldOut: soldOut, nextSaleOpensAt: event.nextSaleOpensAt ?? null }}
              className="shadow-sm"
            />
          </div>
        </div>
        <CardContent className={cn("flex flex-1 flex-col gap-2", variant === "compact" ? "p-3" : "p-4", variant === "large" && "sm:p-6")}>
          <Heading className={cn("line-clamp-2 font-semibold leading-snug text-card-foreground", variant === "large" && "text-lg sm:text-2xl")}>
            {event.title}
          </Heading>
          {/* Sprite icons: inline lucide SVGs would repeat every path in the HTML and RSC payload of each card. */}
          <p className="flex items-center gap-1.5 text-sm text-muted-foreground">
            <SpriteIcon name="calendar-days" className="size-4 shrink-0" />
            {formatDateTime(event.startsAt)}
          </p>
          <p className="flex items-center gap-1.5 text-sm text-muted-foreground">
            <SpriteIcon name="map-pin" className="size-4 shrink-0" />
            <span className="line-clamp-1">
              {event.venueName}, {event.city}
            </span>
          </p>
        </CardContent>
        {event.priceFrom !== null ? (
          <CardFooter className={cn("mt-auto border-t border-border/60", variant === "compact" ? "px-3 py-2.5" : "px-4 py-3")}>
            <Price amount={event.priceFrom} from className="font-semibold text-primary" />
          </CardFooter>
        ) : null}
      </Card>
    </Link>
  );
}
