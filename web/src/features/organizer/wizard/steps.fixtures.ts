import type { OrganizerEvent } from "../types";

/** Test-only: an organizer event with sensible defaults. */
export function eventFixture(patch: Partial<OrganizerEvent> = {}): OrganizerEvent {
  return {
    id: "e1",
    slug: "dem-nhac",
    title: "Đêm nhạc",
    status: "Draft",
    category: "concert",
    venueId: "v1",
    startsAt: "2026-12-06T12:30:00Z",
    endsAt: "2026-12-06T15:30:00Z",
    description: "",
    lineup: [],
    coverImageUrl: null,
    tiers: [],
    layout: null,
    seatsPerTier: {},
    phases: [],
    version: "1",
    ...patch,
  };
}
