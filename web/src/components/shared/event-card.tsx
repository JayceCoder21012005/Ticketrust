import { CalendarDays, MapPin } from "lucide-react";
import Image from "next/image";
import Link from "next/link";
import { formatDateTime } from "@/lib/format";
import { cn } from "@/lib/utils";
import { Price } from "./price";

export type EventCardData = {
  slug: string;
  title: string;
  startsAt: string;
  venueName: string;
  city: string;
  coverImageUrl: string | null;
  priceFrom: number | null;
};

type EventCardProps = {
  event: EventCardData;
  /** Only the first, above-the-fold card should be the LCP candidate. */
  priority?: boolean;
  className?: string;
};

export function EventCard({ event, priority = false, className }: EventCardProps) {
  return (
    <Link
      href={`/events/${event.slug}`}
      className={cn("group flex flex-col overflow-hidden rounded-xl border bg-card transition-shadow hover:shadow-md", className)}
    >
      {/* Fixed 16:9 box reserves space so the grid never shifts (CLS). */}
      <div className="relative aspect-video bg-muted">
        {event.coverImageUrl ? (
          <Image
            src={event.coverImageUrl}
            alt=""
            fill
            priority={priority}
            sizes="(min-width: 1024px) 380px, (min-width: 640px) 50vw, 100vw"
            className="object-cover transition-transform duration-200 group-hover:scale-[1.02]"
          />
        ) : (
          <div aria-hidden className="size-full bg-gradient-to-br from-primary/30 to-primary/5" />
        )}
      </div>
      <div className="flex flex-1 flex-col gap-2 p-4">
        <h3 className="line-clamp-2 font-semibold leading-snug">{event.title}</h3>
        <p className="flex items-center gap-1.5 text-sm text-muted-foreground">
          <CalendarDays aria-hidden className="size-4" />
          {formatDateTime(event.startsAt)}
        </p>
        <p className="flex items-center gap-1.5 text-sm text-muted-foreground">
          <MapPin aria-hidden className="size-4" />
          {event.venueName}, {event.city}
        </p>
        {event.priceFrom !== null ? <Price amount={event.priceFrom} from className="mt-auto pt-2" /> : null}
      </div>
    </Link>
  );
}
