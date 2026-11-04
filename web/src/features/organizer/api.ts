import { api } from "@/lib/api/client";
import { ApiError, type ProblemDetails } from "@/lib/errors";
import type {
  EventStatus,
  OrganizerEvent,
  OrganizerEventItem,
  PhaseInput,
  PublishChecklist,
  SectionDefinition,
  TierInput,
  VenueItem,
} from "./types";

/** Spec 05 organizer api. Every change sends the `version` it edited; a stale one fails with `event.concurrent_edit`. */
function orThrow<T>(result: { data?: unknown; error?: unknown; response: Response }): T {
  if (result.data === undefined) {
    throw new ApiError(result.response.status, (result.error ?? {}) as ProblemDetails);
  }

  return result.data as T;
}

export const organizerKeys = {
  events: (filters: { q: string; status: EventStatus | "all" }) => ["organizer", "events", filters] as const,
  event: (id: string) => ["organizer", "event", id] as const,
  checklist: (id: string) => ["organizer", "checklist", id] as const,
  venues: ["organizer", "venues"] as const,
};

const byId = (id: string) => ({ params: { path: { id } } });

export async function listVenues(): Promise<VenueItem[]> {
  return orThrow(await api.GET("/api/v1/organizer/venues"));
}

export async function createVenue(body: Omit<VenueItem, "id" | "shared">): Promise<VenueItem> {
  return orThrow(await api.POST("/api/v1/organizer/venues", { body }));
}

export async function listEvents(filters: { q: string; status: EventStatus | "all" }): Promise<OrganizerEventItem[]> {
  const query: { q?: string; status?: EventStatus } = {};
  if (filters.q.trim()) query.q = filters.q.trim();
  if (filters.status !== "all") query.status = filters.status;
  return orThrow(await api.GET("/api/v1/organizer/events", { params: { query } }));
}

export type NewEvent = { title: string; category: string; venueId: string; startsAt: string; endsAt: string };

export async function createEvent(body: NewEvent): Promise<OrganizerEvent> {
  return orThrow(await api.POST("/api/v1/organizer/events", { body }));
}

export async function getEvent(id: string): Promise<OrganizerEvent> {
  return orThrow(await api.GET("/api/v1/organizer/events/{id}", byId(id)));
}

export type InfoInput = { version: string; title: string; description: string; lineup: string[]; coverImageUrl: string | null };

export async function putInfo(id: string, body: InfoInput): Promise<OrganizerEvent> {
  return orThrow(await api.PUT("/api/v1/organizer/events/{id}/info", { ...byId(id), body }));
}

export type ScheduleInput = { version: string; category: string; venueId: string; startsAt: string; endsAt: string };

export async function putSchedule(id: string, body: ScheduleInput): Promise<OrganizerEvent> {
  return orThrow(await api.PUT("/api/v1/organizer/events/{id}/schedule", { ...byId(id), body }));
}

export async function putTiers(id: string, version: string, tiers: TierInput[]): Promise<OrganizerEvent> {
  return orThrow(await api.PUT("/api/v1/organizer/events/{id}/tiers", { ...byId(id), body: { version, tiers } }));
}

export async function putLayout(id: string, version: string, sections: SectionDefinition[]): Promise<OrganizerEvent> {
  return orThrow(await api.PUT("/api/v1/organizer/events/{id}/layout", { ...byId(id), body: { version, sections } }));
}

export async function putPhases(id: string, version: string, phases: PhaseInput[]): Promise<OrganizerEvent> {
  return orThrow(await api.PUT("/api/v1/organizer/events/{id}/phases", { ...byId(id), body: { version, phases } }));
}

export async function getChecklist(id: string): Promise<PublishChecklist> {
  return orThrow(await api.GET("/api/v1/organizer/events/{id}/checklist", byId(id)));
}

export async function publishEvent(id: string): Promise<OrganizerEvent> {
  return orThrow(await api.POST("/api/v1/organizer/events/{id}/publish", byId(id)));
}

export async function cancelEvent(id: string): Promise<OrganizerEvent> {
  return orThrow(await api.POST("/api/v1/organizer/events/{id}/cancel", byId(id)));
}

/** Presigned upload (same flow as avatars): ask for a one-file URL, PUT the bytes there, return the public URL. */
export async function uploadCover(id: string, file: Blob, onProgress?: (fraction: number) => void): Promise<string> {
  const ticket = orThrow<{ uploadUrl: string; publicUrl: string }>(
    await api.POST("/api/v1/organizer/events/{id}/cover-upload-url", { ...byId(id), body: { contentType: file.type, size: file.size } }),
  );
  const target = new URL(ticket.uploadUrl);
  await putWithProgress(`${target.pathname}${target.search}`, file, onProgress);
  return ticket.publicUrl;
}

/** fetch() has no upload progress; XMLHttpRequest does. */
function putWithProgress(url: string, file: Blob, onProgress?: (fraction: number) => void): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("PUT", url);
    xhr.setRequestHeader("Content-Type", file.type);
    xhr.upload.onprogress = (e) => e.lengthComputable && onProgress?.(e.loaded / e.total);
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        resolve();
        return;
      }

      let problem: ProblemDetails = {};
      try {
        problem = JSON.parse(xhr.responseText) as ProblemDetails;
      } catch {
        // Not JSON: keep the status only.
      }

      reject(new ApiError(xhr.status, problem));
    };
    xhr.onerror = () => reject(new ApiError(0, {}));
    xhr.send(file);
  });
}
