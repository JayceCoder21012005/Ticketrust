import type { BrowserContext, Page, Route } from "@playwright/test";
import { generateSeats } from "../src/lib/organizer/seat-layout";
import type { OrganizerEvent, PhaseInput, SectionDefinition, TierInput, VenueItem } from "../src/features/organizer/types";

/**
 * In-memory stand-in for the spec 05 organizer api, installed at the browser's network layer (the organizer pages
 * call the api from the client). It keeps the api's rules the e2e flows rely on: versions, the publish checklist and
 * the draft-only freeze. The .NET integration tests cover the real api.
 */
export const VENUES: VenueItem[] = [
  { id: "v1", name: "Nhà hát Lớn Hà Nội", address: "1 Tràng Tiền", city: "Hà Nội", mapUrl: null, capacity: 600, shared: true },
  { id: "v2", name: "Nhà hát Hoà Bình", address: "240 Ba Tháng Hai", city: "Hồ Chí Minh", mapUrl: null, capacity: 2000, shared: true },
];

const DAY = 86_400_000;

export function draftEvent(patch: Partial<OrganizerEvent> = {}): OrganizerEvent {
  const startsAt = new Date(Date.now() + 30 * DAY).toISOString();
  return {
    id: "e1",
    slug: "dem-nhac-thu",
    title: "Đêm nhạc thử",
    status: "Draft",
    category: "concert",
    venueId: "v1",
    startsAt,
    endsAt: new Date(Date.parse(startsAt) + 3 * 3_600_000).toISOString(),
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

const loginUser = (roles: string[]) => ({
  accessToken: "e2e-organizer-token",
  expiresAt: new Date(Date.now() + 600_000).toISOString(),
  user: { id: "u1", email: "org@example.com", fullName: "Trần Tổ Chức", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles },
});

/** A restored session (spec 03): the hint cookie makes the app call refresh, which answers with this user. */
export async function signInAs(context: BrowserContext, page: Page, baseURL: string, roles: string[] = ["Organizer"]) {
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await page.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: loginUser(roles) }));
}

export class OrganizerFake {
  events = new Map<string, OrganizerEvent>();
  private seq = 1;

  constructor(...events: OrganizerEvent[]) {
    for (const event of events) this.events.set(event.id, event);
  }

  async install(page: Page) {
    await page.route("**/api/v1/organizer/**", (route) => this.handle(route));
    // Presigned cover upload: accept the bytes.
    await page.route("**/api/v1/storage/uploads**", (route) => route.fulfill({ status: 204 }));
  }

  private save(event: OrganizerEvent): OrganizerEvent {
    const seats = event.layout ? generateSeats(event.layout) : [];
    const seatsPerTier: Record<string, number> = {};
    for (const seat of seats) seatsPerTier[seat.tierId] = (seatsPerTier[seat.tierId] ?? 0) + 1;
    const saved = { ...event, seatsPerTier, version: String(Number(event.version) + 1) };
    this.events.set(saved.id, saved);
    return saved;
  }

  checklist(event: OrganizerEvent) {
    const failures: { code: string; step: string; message: string }[] = [];
    if (!event.coverImageUrl) failures.push({ code: "publish.missing_cover", step: "info", message: "" });
    if (event.tiers.length === 0) failures.push({ code: "publish.no_tiers", step: "tiers", message: "" });
    if (event.tiers.some((t) => t.kind === "Seated" && !event.seatsPerTier[t.id])) failures.push({ code: "publish.tier_without_seats", step: "layout", message: "" });
    if (event.phases.length === 0) failures.push({ code: "publish.no_phases", step: "phases", message: "" });
    else if (Math.min(...event.phases.map((p) => Date.parse(p.opensAt))) - Date.now() < 10 * 60_000)
      failures.push({ code: "publish.first_phase_too_soon", step: "phases", message: "" });
    return { ready: failures.length === 0, failures };
  }

  private async handle(route: Route) {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname.replace("/api/v1/organizer", "");
    const method = request.method();
    const body = method === "GET" ? null : (request.postDataJSON() as Record<string, unknown> | null);
    const json = (data: unknown, status = 200) => route.fulfill({ status, json: data });

    if (path === "/venues" && method === "GET") return json(VENUES);
    if (path === "/events" && method === "GET") {
      return json(
        [...this.events.values()].map((e) => ({
          id: e.id,
          slug: e.slug,
          title: e.title,
          status: e.status,
          startsAt: e.startsAt,
          venueName: VENUES.find((v) => v.id === e.venueId)?.name ?? "",
          seatCount: Object.values(e.seatsPerTier).reduce((a, b) => a + b, 0),
          coverImageUrl: e.coverImageUrl,
        })),
      );
    }
    if (path === "/events" && method === "POST") {
      const id = `new-${this.seq++}`;
      const created = draftEvent({ id, slug: id, version: "0", ...(body as Partial<OrganizerEvent>) });
      return json(this.save(created), 201);
    }

    const match = /^\/events\/([^/]+)(?:\/(.+))?$/.exec(path);
    const event = match ? this.events.get(match[1]) : undefined;
    if (!match || !event) return json({ status: 404, code: "event.not_found" }, 404);
    const action = match[2];

    if (!action && method === "GET") return json(event);
    if (action === "checklist") return json(this.checklist(event));
    if (action === "cover-upload-url") {
      return json({ uploadUrl: `${url.origin}/api/v1/storage/uploads?token=e2e`, publicUrl: `${url.origin}/favicon.ico`, expiresAt: new Date(Date.now() + 600_000).toISOString() });
    }
    if (action === "publish") {
      const checklist = this.checklist(event);
      if (!checklist.ready) return json({ status: 422, code: "event.publish_checklist_failed", failures: checklist.failures }, 422);
      return json(event.status === "Published" ? event : this.save({ ...event, status: "Published" }));
    }
    if (action === "cancel") return json(this.save({ ...event, status: "Cancelled" }));

    // Every PUT: the version must match, inventory is draft-only.
    if (body?.version !== event.version) return json({ status: 409, code: "event.concurrent_edit" }, 409);
    const frozen = event.status !== "Draft";
    switch (action) {
      case "info":
        return json(this.save({ ...event, ...(body as Partial<OrganizerEvent>), version: event.version }));
      case "schedule":
        return frozen ? json({ status: 409, code: "event.not_editable" }, 409) : json(this.save({ ...event, ...(body as Partial<OrganizerEvent>), version: event.version }));
      case "tiers": {
        if (frozen) return json({ status: 409, code: "event.not_editable" }, 409);
        const tiers = (body.tiers as TierInput[]).map((t) => ({ ...t, id: t.id ?? `tier-${this.seq++}` }));
        return json(this.save({ ...event, tiers }));
      }
      case "layout":
        return frozen ? json({ status: 409, code: "event.not_editable" }, 409) : json(this.save({ ...event, layout: { sections: body.sections as SectionDefinition[] } }));
      case "phases": {
        if (frozen) return json({ status: 409, code: "event.not_editable" }, 409);
        const phases = (body.phases as PhaseInput[]).map((p) => {
          const old = event.phases.find((x) => x.id === p.id);
          return {
            id: p.id ?? `phase-${this.seq++}`,
            name: p.name,
            opensAt: new Date(p.opensAt).toISOString(),
            closesAt: new Date(p.closesAt).toISOString(),
            queueEnabled: p.queueEnabled,
            maxTicketsPerUser: p.maxTicketsPerUser,
            hasAccessCode: p.accessCode === null ? (old?.hasAccessCode ?? false) : p.accessCode !== "",
          };
        });
        return json(this.save({ ...event, phases }));
      }
    }

    return json({ status: 404 }, 404);
  }
}
