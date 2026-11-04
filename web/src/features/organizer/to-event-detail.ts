import type { EventDetail } from "@/lib/catalog/types";
import type { OrganizerEvent, VenueItem } from "./types";

/** The draft as the spec 04 detail page expects it, so the preview is the real page, not a look-alike. */
export function toEventDetail(event: OrganizerEvent, venue: VenueItem | undefined): EventDetail {
  return {
    id: event.id,
    slug: event.slug,
    title: event.title,
    description: event.description,
    category: event.category,
    lineup: event.lineup,
    coverImageUrl: event.coverImageUrl,
    startsAt: event.startsAt,
    endsAt: event.endsAt,
    status: event.status,
    isSoldOut: false,
    venue: venue
      ? { id: venue.id, name: venue.name, address: venue.address, city: venue.city, mapUrl: venue.mapUrl }
      : { id: event.venueId, name: "Địa điểm", address: "", city: "", mapUrl: null },
    tiers: event.tiers.map((t) => ({ id: t.id, name: t.name, price: t.price, currency: "VND", kind: t.kind, color: t.color })),
    phases: event.phases.map((p) => ({
      id: p.id,
      name: p.name,
      opensAt: p.opensAt,
      closesAt: p.closesAt,
      queueEnabled: p.queueEnabled,
      maxTicketsPerUser: p.maxTicketsPerUser,
      requiresAccessCode: p.hasAccessCode,
    })),
  };
}
