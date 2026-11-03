import { cacheLife, cacheTag } from "next/cache";
import { DEFAULT_API_URL } from "@/lib/api-rewrites";
import type { EventDetail, EventPage, EventSummary } from "./types";

/**
 * Server-side catalog reads (spec 04). Each is a `"use cache"` function tagged like the api's HybridCache, so the
 * revalidate hook (`POST /api/revalidate`) evicts both layers. Pages render them without request-time APIs, so
 * Next serves them as ISR pages: complete static HTML, regenerated in the background when stale.
 *
 * At build time the api may be unreachable (Docker image builds). Then the read returns an empty result with
 * the `seconds` profile, which makes that part of the page a dynamic hole rendered per request instead of
 * baking an empty catalog into the build.
 */
const apiOrigin = () => (process.env.API_URL || DEFAULT_API_URL).replace(/\/+$/, "");
const isBuild = () => process.env.NEXT_PHASE === "phase-production-build";

export const LIST_TAG = "events:list";
export const eventTag = (id: string) => `event:${id}`;
export const slugTag = (slug: string) => `event-slug:${slug}`;

/** Slugs are lower-case words joined by dashes; anything else is a 404 without asking the api. */
export const isSlug = (value: string) => /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(value) && value.length <= 160;

const UNAVAILABLE = Symbol("api unavailable at build time");

async function getJson<T>(path: string): Promise<T | null | typeof UNAVAILABLE> {
  let response: Response;
  try {
    response = await fetch(`${apiOrigin()}/api/v1${path}`, { headers: { accept: "application/json" } });
  } catch (error) {
    if (isBuild()) {
      return UNAVAILABLE;
    }

    throw error;
  }

  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`GET /api/v1${path} failed with ${response.status}`);
  }

  return (await response.json()) as T;
}

export async function getEventPage(query: Record<string, string>): Promise<EventPage> {
  "use cache";
  cacheTag(LIST_TAG);
  const search = new URLSearchParams(query).toString();
  const page = await getJson<EventPage>(`/events${search ? `?${search}` : ""}`);
  if (page === UNAVAILABLE) {
    cacheLife("seconds");
    return { items: [], nextCursor: null };
  }

  cacheLife({ stale: 30, revalidate: 30, expire: 3600 });
  return page ?? { items: [], nextCursor: null };
}

export async function getEvent(slug: string): Promise<EventDetail | null> {
  "use cache";
  cacheTag(slugTag(slug));
  const event = await getJson<EventDetail>(`/events/${encodeURIComponent(slug)}`);
  if (event === UNAVAILABLE) {
    cacheLife("seconds");
    return null;
  }

  cacheLife({ stale: 60, revalidate: 600, expire: 3600 });
  if (event) {
    cacheTag(eventTag(event.id));
  }

  return event;
}

export async function getCities(): Promise<string[]> {
  "use cache";
  cacheTag(LIST_TAG);
  const cities = await getJson<string[]>("/cities");
  if (cities === UNAVAILABLE) {
    cacheLife("seconds");
    return [];
  }

  cacheLife({ stale: 300, revalidate: 600, expire: 3600 });
  return cities ?? [];
}

/** Slugs prerendered by `next build` (the soonest events); none when the api is unreachable. */
export async function getPrerenderSlugs(limit: number): Promise<string[]> {
  const page = await getJson<EventPage>(`/events?limit=${limit}`).catch(() => null);
  return page && page !== UNAVAILABLE ? page.items.map((event: EventSummary) => event.slug) : [];
}

/** Server clock for presets ("Hôm nay"); the api is the single source of time (C10). */
export async function getServerNow(): Promise<Date> {
  const time = await getJson<{ serverNow: string }>("/time").catch(() => null);
  return time && time !== UNAVAILABLE ? new Date(time.serverNow) : new Date();
}
