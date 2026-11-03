"use server";

import type { ReactNode } from "react";
import { EventCard } from "@/components/shared/event-card";
import { getEventPage } from "@/lib/catalog/api";

const QUERY_KEYS = new Set(["q", "category", "city", "from", "to", "priceMax"]);
const PAGE_SIZE = "9";

/**
 * Next page of the listing, rendered on the server: the browser receives finished cards instead of the card,
 * image and date-formatting code (~15 KB of first-load JS). Inputs come from the client, so only known filter
 * keys pass through; the api validates the values.
 */
export async function loadMoreEvents(query: Record<string, string>, cursor: string): Promise<{ cards: ReactNode; nextCursor: string | null }> {
  if (typeof cursor !== "string" || cursor.length === 0 || cursor.length > 512) {
    throw new Error("Invalid cursor.");
  }

  const filters = Object.fromEntries(
    Object.entries(query ?? {}).filter(([key, value]) => QUERY_KEYS.has(key) && typeof value === "string" && value.length <= 200),
  );
  const page = await getEventPage({ ...filters, cursor, limit: PAGE_SIZE });

  return {
    cards: page.items.map((event) => (
      <li key={event.id}>
        <EventCard event={event} headingLevel="h2" className="h-full" />
      </li>
    )),
    nextCursor: page.nextCursor,
  };
}
