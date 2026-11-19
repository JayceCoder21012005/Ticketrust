/**
 * Catalog shapes as the api serializes them (spec 04). Hand-written instead of the generated `schema.d.ts`
 * types because those widen every int64 to `number | string` (the api only ever sends numbers).
 */
export type EventStatus = "Draft" | "Published" | "Ended" | "Cancelled";
export type TicketKind = "Seated" | "GeneralAdmission";

export type EventSummary = {
  id: string;
  slug: string;
  title: string;
  category: string;
  startsAt: string;
  venueName: string;
  city: string;
  coverImageUrl: string | null;
  /** Cheapest tier in whole dong; null when the event has no tiers yet. */
  priceFrom: number | null;
  nextSaleOpensAt: string | null;
  isOnSale: boolean;
  isSoldOut: boolean;
};

export type EventPage = { items: EventSummary[]; nextCursor: string | null };

/** The home's sections, picked on the server (UI refresh, `GET /catalog/home`). */
export type HomeSections = {
  hot: EventSummary[];
  openingSoon: EventSummary[];
  thisWeek: EventSummary[];
  upcoming: EventSummary[];
  cities: { city: string; events: number }[];
  categories: { category: string; events: number }[];
};

export type Venue = { id: string; name: string; address: string; city: string; mapUrl: string | null };

export type TicketTier = { id: string; name: string; price: number; currency: string; kind: TicketKind; color: string };

export type SalePhase = {
  id: string;
  name: string;
  opensAt: string;
  closesAt: string;
  queueEnabled: boolean;
  maxTicketsPerUser: number;
  requiresAccessCode: boolean;
};

export type EventDetail = {
  id: string;
  slug: string;
  title: string;
  description: string;
  category: string;
  lineup: string[];
  coverImageUrl: string | null;
  startsAt: string;
  endsAt: string;
  status: EventStatus;
  isSoldOut: boolean;
  venue: Venue;
  tiers: TicketTier[];
  phases: SalePhase[];
};

export type Category = { slug: string; name: string };

export type TierRemaining = { tierId: string; remaining: number };

export type Availability = { serverNow: string; version: number; unavailable: number[]; tiers: TierRemaining[] };
