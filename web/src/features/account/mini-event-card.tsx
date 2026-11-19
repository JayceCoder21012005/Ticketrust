import { CalendarDays } from "lucide-react";
import Link from "next/link";
import { EventPoster } from "@/components/catalog/event-poster";
import type { EventSummary } from "@/lib/catalog/types";
import { formatDateTime } from "@/lib/format";

/** A light event card for client-rendered rows (account hub, "for you"): poster, title, date; the whole card is the link. */
export function MiniEventCard({ event }: { event: Pick<EventSummary, "slug" | "title" | "category" | "startsAt" | "coverImageUrl"> }) {
  return (
    <Link href={`/events/${event.slug}`} prefetch={false} className="group/mini flex flex-col gap-2 rounded-xl outline-none focus-visible:ring-2 focus-visible:ring-ring">
      <div className="relative aspect-[4/3] overflow-hidden rounded-xl bg-muted">
        <EventPoster
          slug={event.slug}
          category={event.category}
          title={event.title}
          coverImageUrl={event.coverImageUrl}
          sizes="200px"
          className="motion-safe:transition-transform motion-safe:group-hover/mini:scale-[1.04]"
        />
      </div>
      <span className="line-clamp-2 text-sm font-semibold leading-snug">{event.title}</span>
      <span className="flex items-center gap-1 text-xs text-muted-foreground">
        <CalendarDays aria-hidden className="size-3.5" />
        {formatDateTime(event.startsAt)}
      </span>
    </Link>
  );
}
