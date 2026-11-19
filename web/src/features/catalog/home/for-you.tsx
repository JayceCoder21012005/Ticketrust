"use client";

import { useEffect, useState } from "react";
import { MiniEventCard } from "@/features/account/mini-event-card";
import { api } from "@/lib/api/client";
import { hasSessionHint } from "@/lib/auth/session-hint";
import type { EventSummary } from "@/lib/catalog/types";
import { t } from "@/lib/i18n";

const MAX = 6;
const MAX_CATEGORIES = 2;

/**
 * "For you" (UI refresh): for a signed-in buyer, events in the categories of the events they follow, minus those they follow
 * and those they already hold tickets for. Loaded after hydration, so the home stays a static page; renders nothing (no
 * heading, no reserved space) until there is something to show.
 */
export function ForYou() {
  const [events, setEvents] = useState<EventSummary[]>([]);

  useEffect(() => {
    if (!hasSessionHint(document.cookie)) return;
    let alive = true;
    void (async () => {
      try {
        const follows = (await api.GET("/api/v1/me/follows")).data as EventSummary[] | undefined;
        if (!follows?.length) return;
        const tickets = (await api.GET("/api/v1/me/tickets", { params: { query: { scope: "upcoming" } } })).data as { events: { slug: string }[] } | undefined;
        const skip = new Set([...follows.map((e) => e.slug), ...(tickets?.events ?? []).map((g) => g.slug)]);
        const categories = [...new Set(follows.map((e) => e.category))].slice(0, MAX_CATEGORIES);
        const pages = await Promise.all(
          categories.map((category) => api.GET("/api/v1/events", { params: { query: { category, limit: 12 } } }).then((r) => (r.data?.items ?? []) as EventSummary[])),
        );
        const picked = pages.flat().filter((e, i, all) => !skip.has(e.slug) && all.findIndex((x) => x.slug === e.slug) === i);
        if (alive) setEvents(picked.slice(0, MAX));
      } catch {
        // A suggestion row is optional: on any failure the home simply goes without it.
      }
    })();
    return () => {
      alive = false;
    };
  }, []);

  if (events.length === 0) return null;

  return (
    <section aria-labelledby="for-you" className="flex flex-col gap-4">
      <div className="flex flex-col gap-1">
        <h2 id="for-you" className="text-xl font-bold tracking-tight sm:text-2xl">
          {t("home.forYou")}
        </h2>
        <p className="text-sm text-muted-foreground">{t("home.forYouDescription")}</p>
      </div>
      <ul className="-mx-4 flex snap-x gap-3 overflow-x-auto px-4 pb-2 sm:mx-0 sm:grid sm:grid-cols-3 sm:overflow-visible sm:px-0 lg:grid-cols-6">
        {events.map((event) => (
          <li key={event.slug} className="w-[60%] shrink-0 snap-start sm:w-auto">
            <MiniEventCard event={event} />
          </li>
        ))}
      </ul>
    </section>
  );
}
