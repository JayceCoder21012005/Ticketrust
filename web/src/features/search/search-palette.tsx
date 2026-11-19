"use client";

import { Building2, CalendarDays, Clock, Flame, MapPin, Mic2, Search } from "lucide-react";
import { useRouter } from "next/navigation";
import { useState, type ReactNode } from "react";
import { Command, CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList, CommandSeparator } from "@/components/ui/command";
import { foldChar, type EventSuggestion } from "@/lib/catalog/search";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n";
import { recentSearches } from "./recent-searches";
import { useSuggestions } from "./use-suggestions";

/** Wraps the parts of `text` that match a word of `query` in <mark>, ignoring accents and case ("son tung" marks "Sơn Tùng"). */
export function highlight(text: string, query: string): ReactNode {
  const folded = [...text].map(foldChar).join("");
  const words = [...new Set([...query].map(foldChar).join("").split(/[^a-z0-9]+/).filter((w) => w.length > 0))];
  const marked = new Array<boolean>(folded.length).fill(false);
  for (const word of words) {
    for (let at = folded.indexOf(word); at >= 0; at = folded.indexOf(word, at + word.length)) {
      marked.fill(true, at, at + word.length);
    }
  }

  const chars = [...text];
  const parts: ReactNode[] = [];
  let start = 0;
  for (let i = 1; i <= chars.length; i++) {
    if (i === chars.length || marked[i] !== marked[start]) {
      const piece = chars.slice(start, i).join("");
      parts.push(marked[start] ? <mark key={start} className="rounded-sm bg-primary/15 px-0.5 text-foreground">{piece}</mark> : piece);
      start = i;
    }
  }

  return parts;
}

type Props = { open: boolean; onOpenChange: (open: boolean) => void };

/**
 * The ⌘K search palette (UI refresh): suggestions while typing, grouped by events, artists, venues and cities, matches
 * highlighted, arrows + Enter to go, recent searches on an empty input, and a way to every result on the listing page.
 * The server ranks; cmdk only handles the keyboard (`shouldFilter={false}`). Loaded lazily by SearchTrigger.
 */
export function SearchPalette({ open, onOpenChange }: Props) {
  const router = useRouter();
  const [query, setQuery] = useState("");
  const [recent, setRecent] = useState(recentSearches.get);
  const { data } = useSuggestions(query);
  const typed = query.trim();

  const go = (href: string) => {
    if (typed) recentSearches.add(typed);
    onOpenChange(false);
    router.push(href);
  };

  const events = data?.events ?? [];
  const count = typed ? events.length + (data?.artists.length ?? 0) + (data?.venues.length ?? 0) + (data?.cities.length ?? 0) : 0;

  // Results arrive after the keystroke: cmdk only auto-selects on its own filtering, so put the highlight on the first result.
  const first = typed
    ? events[0]
      ? `event:${events[0].slug}`
      : data?.artists[0]
        ? `artist:${data.artists[0]}`
        : data?.venues[0]
          ? `venue:${data.venues[0].name}`
          : data?.cities[0]
            ? `city:${data.cities[0]}`
            : "see-all"
    : "";
  const [selected, setSelected] = useState("");
  const [selectedFor, setSelectedFor] = useState("");
  if (selectedFor !== first) {
    setSelectedFor(first);
    setSelected(first);
  }

  return (
    <CommandDialog open={open} onOpenChange={onOpenChange} title={t("search.title")} description={t("search.description")} className="sm:max-w-xl">
      <Command shouldFilter={false} loop value={selected} onValueChange={setSelected}>
        <CommandInput value={query} onValueChange={setQuery} placeholder={t("common.search")} maxLength={100} />
        <p role="status" className="sr-only">
          {typed ? t("search.resultCount", "vi", { count }) : ""}
        </p>
        <CommandList className="max-h-[min(70vh,28rem)]">
          {typed ? <CommandEmpty>{t("search.empty")}</CommandEmpty> : null}

          {!typed && recent.length > 0 ? (
            <div className="flex items-center justify-between px-3 pt-2 text-xs font-medium text-muted-foreground">
              <span aria-hidden>{t("search.recent")}</span>
              <button
                type="button"
                className="rounded px-1 font-medium text-primary hover:underline"
                aria-label={t("search.clearRecent")}
                onClick={() => {
                  recentSearches.clear();
                  setRecent([]);
                }}
              >
                {t("search.clear")}
              </button>
            </div>
          ) : null}

          {!typed && recent.length > 0 ? (
            <CommandGroup aria-label={t("search.recent")}>
              {recent.map((q) => (
                <CommandItem key={q} value={`recent:${q}`} onSelect={() => setQuery(q)}>
                  <Clock aria-hidden />
                  {q}
                </CommandItem>
              ))}
            </CommandGroup>
          ) : null}

          {events.length > 0 ? (
            <CommandGroup heading={typed ? t("search.events") : t("search.trending")}>
              {events.map((event) => (
                <EventItem key={event.slug} event={event} query={typed} onSelect={() => go(`/events/${event.slug}`)} trending={!typed} />
              ))}
            </CommandGroup>
          ) : null}

          {typed && data && data.artists.length > 0 ? (
            <CommandGroup heading={t("search.artists")}>
              {data.artists.map((name) => (
                <CommandItem key={name} value={`artist:${name}`} onSelect={() => go(`/events?${new URLSearchParams({ q: name })}`)}>
                  <Mic2 aria-hidden />
                  <span>{highlight(name, typed)}</span>
                </CommandItem>
              ))}
            </CommandGroup>
          ) : null}

          {typed && data && data.venues.length > 0 ? (
            <CommandGroup heading={t("search.venues")}>
              {data.venues.map((venue) => (
                <CommandItem key={venue.name} value={`venue:${venue.name}`} onSelect={() => go(`/events?${new URLSearchParams({ q: venue.name })}`)}>
                  <Building2 aria-hidden />
                  <span className="flex-1">{highlight(venue.name, typed)}</span>
                  <span className="text-xs text-muted-foreground">{venue.city}</span>
                </CommandItem>
              ))}
            </CommandGroup>
          ) : null}

          {typed && data && data.cities.length > 0 ? (
            <CommandGroup heading={t("search.cities")}>
              {data.cities.map((city) => (
                <CommandItem key={city} value={`city:${city}`} onSelect={() => go(`/events?${new URLSearchParams({ city })}`)}>
                  <MapPin aria-hidden />
                  <span>{highlight(city, typed)}</span>
                </CommandItem>
              ))}
            </CommandGroup>
          ) : null}

          {typed ? (
            <>
              <CommandSeparator />
              <CommandGroup>
                <CommandItem value="see-all" onSelect={() => go(`/events?${new URLSearchParams({ q: typed })}`)}>
                  <Search aria-hidden />
                  {t("search.seeAll", "vi", { q: typed })}
                </CommandItem>
              </CommandGroup>
            </>
          ) : null}
        </CommandList>
      </Command>
    </CommandDialog>
  );
}

function EventItem({ event, query, trending, onSelect }: { event: EventSuggestion; query: string; trending: boolean; onSelect: () => void }) {
  return (
    <CommandItem value={`event:${event.slug}`} onSelect={onSelect} className="items-start py-2">
      {trending ? <Flame aria-hidden className="mt-0.5 text-live" /> : <CalendarDays aria-hidden className="mt-0.5" />}
      <span className="flex min-w-0 flex-col">
        <span className="truncate font-medium">{query ? highlight(event.title, query) : event.title}</span>
        <span className="truncate text-xs text-muted-foreground">
          {formatDateTime(event.startsAt)} · {event.venueName}, {event.city}
        </span>
      </span>
    </CommandItem>
  );
}

export default SearchPalette;
