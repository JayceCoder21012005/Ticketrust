/**
 * The organizer api's shapes (spec 05) with plain numbers: the generated schema types every integer as
 * `number | string`, but the api always sends JSON numbers.
 */
export type EventStatus = "Draft" | "Published" | "Ended" | "Cancelled";
export type TicketKind = "Seated" | "GeneralAdmission";
export type SectionKind = "seated" | "standing";

export type VenueItem = { id: string; name: string; address: string; city: string; mapUrl: string | null; capacity: number; shared: boolean };

export type OrganizerEventItem = {
  id: string;
  slug: string;
  title: string;
  status: EventStatus;
  startsAt: string;
  venueName: string;
  seatCount: number;
  coverImageUrl: string | null;
};

export type TierItem = { id: string; name: string; price: number; kind: TicketKind; capacity: number | null; color: string };
export type TierInput = Omit<TierItem, "id"> & { id: string | null };

export type LayoutPoint = { x: number; y: number };

export type SectionDefinition = {
  code: string;
  tierId: string;
  kind: SectionKind;
  rows?: number | null;
  seatsPerRow?: number | null;
  origin?: LayoutPoint | null;
  curve: number;
};

export type SeatLayoutDefinition = { sections: SectionDefinition[] };

export type PhaseItem = {
  id: string;
  name: string;
  opensAt: string;
  closesAt: string;
  queueEnabled: boolean;
  maxTicketsPerUser: number;
  hasAccessCode: boolean;
};

/** `accessCode`: null keeps the phase's current code, "" removes it, a value sets a new one. */
export type PhaseInput = Omit<PhaseItem, "id" | "hasAccessCode"> & { id: string | null; accessCode: string | null };

export type OrganizerEvent = {
  id: string;
  slug: string;
  title: string;
  status: EventStatus;
  category: string;
  venueId: string;
  startsAt: string;
  endsAt: string;
  description: string;
  lineup: string[];
  coverImageUrl: string | null;
  tiers: TierItem[];
  layout: SeatLayoutDefinition | null;
  seatsPerTier: Record<string, number>;
  phases: PhaseItem[];
  version: string;
};

export type WizardStep = "info" | "tiers" | "layout" | "phases" | "review";

export type PublishFailure = { code: string; step: Exclude<WizardStep, "review">; message: string };
export type PublishChecklist = { ready: boolean; failures: PublishFailure[] };
