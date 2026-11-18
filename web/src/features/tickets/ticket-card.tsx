import { CalendarDays, MapPin, Ticket } from "lucide-react";
import Link from "next/link";
import { EventPoster } from "@/components/catalog/event-poster";
import { Badge } from "@/components/ui/badge";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n.buyer";
import type { TicketEventGroup, TicketScope } from "@/lib/tickets/types";

type TicketCardProps = {
  group: TicketEventGroup;
  scope: TicketScope;
  /** Only the first card of the first screen is a candidate for the LCP image. */
  priority?: boolean;
};

/** The chip of an event the buyer can no longer attend; null for one that is still to come. */
function statusLabel(group: TicketEventGroup, scope: TicketScope): string | null {
  if (group.status === "Cancelled") return t("tickets.status.cancelled");
  if (group.status === "Ended" || Date.parse(group.endsAt) <= Date.now()) return t("tickets.status.ended");
  // In the past tab but the event has not ended: every ticket of it has been voided (refunded or cancelled).
  return scope === "past" ? t("tickets.status.voided") : null;
}

/** "Hạng VIP, Hạng thường": each tier once, in the order of the tickets. */
function tierSummary(group: TicketEventGroup): string {
  return [...new Set(group.tickets.map((ticket) => ticket.tierName))].join(", ");
}

/**
 * One event of the wallet. The whole card is the link to the wallet screen (`/tickets/{id}`) of the first ticket; the
 * wallet's own navigation reaches the other tickets of the order. The title is the link, stretched over the card.
 */
export function TicketCard({ group, scope, priority = false }: TicketCardProps) {
  const [first] = group.tickets;
  if (!first) return null;

  const chip = statusLabel(group, scope);
  const seat = first.seat ? t("tickets.seat", "vi", first.seat) : null;
  const detail = group.tickets.length === 1 ? [first.tierName, seat].filter(Boolean).join(" · ") : tierSummary(group);

  return (
    <li className="group relative flex flex-col overflow-hidden rounded-xl border bg-card transition-shadow focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-ring hover:shadow-md sm:flex-row">
      {/* Fixed 16:9 box (a fixed-width one beside the text from sm up) reserves the space so the list never shifts. */}
      <div className="relative aspect-video shrink-0 overflow-hidden bg-muted sm:aspect-auto sm:w-56">
        <EventPoster
          slug={group.slug}
          category=""
          title={group.title}
          coverImageUrl={group.coverImageUrl}
          priority={priority}
          sizes="(min-width: 640px) 224px, 100vw"
        />
      </div>
      <div className="flex min-w-0 flex-1 flex-col gap-2 p-4">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <h2 className="line-clamp-2 min-w-0 font-semibold leading-snug">
            <Link
              href={`/tickets/${first.id}`}
              prefetch={false}
              className="outline-none after:absolute after:inset-0 after:content-['']"
            >
              {group.title}
            </Link>
          </h2>
          {chip ? (
            <Badge variant="outline" className="h-6 px-2.5 text-foreground">
              {chip}
            </Badge>
          ) : null}
        </div>
        <p className="flex items-center gap-1.5 text-sm text-muted-foreground">
          <CalendarDays aria-hidden className="size-4 shrink-0" />
          {formatDateTime(group.startsAt)}
        </p>
        <p className="flex items-start gap-1.5 text-sm text-muted-foreground">
          <MapPin aria-hidden className="mt-0.5 size-4 shrink-0" />
          <span className="min-w-0 break-words">{`${group.venueName}, ${group.city}`}</span>
        </p>
        <p className="mt-auto flex flex-wrap items-center gap-x-2 gap-y-1 pt-2 text-sm">
          <Ticket aria-hidden className="size-4 shrink-0 text-primary" />
          <strong className="font-semibold">{t("tickets.count", "vi", { count: group.tickets.length })}</strong>
          {detail ? <span className="min-w-0 break-words text-muted-foreground">{detail}</span> : null}
        </p>
      </div>
    </li>
  );
}
