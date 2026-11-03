"use client";

import { Search } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { listingHref, type EventFilters } from "@/lib/catalog/filters";
import { t } from "@/lib/i18n";

const DEBOUNCE_MS = 300;

type SearchBoxProps = { filters: EventFilters };

/**
 * Search as you type (debounced 300 ms, spec 04). Also a real GET form, so Enter works before hydration and
 * the query string stays the single source of truth.
 */
export function SearchBox({ filters }: SearchBoxProps) {
  const router = useRouter();
  const [value, setValue] = useState(filters.q ?? "");
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);

  useEffect(() => () => clearTimeout(timer.current), []);

  const search = (q: string) => {
    clearTimeout(timer.current);
    router.replace(listingHref({ ...filters, q: q.trim() || undefined }), { scroll: false });
  };

  return (
    <form
      role="search"
      action="/events"
      onSubmit={(e) => {
        e.preventDefault();
        search(value);
      }}
      className="relative w-full"
    >
      <Search aria-hidden className="pointer-events-none absolute left-3 top-1/2 size-5 -translate-y-1/2 text-muted-foreground" />
      <input
        type="search"
        name="q"
        value={value}
        maxLength={100}
        aria-label={t("common.searchLabel")}
        placeholder={t("catalog.listing.searchPlaceholder")}
        onChange={(e) => {
          const next = e.target.value;
          setValue(next);
          clearTimeout(timer.current);
          timer.current = setTimeout(() => search(next), DEBOUNCE_MS);
        }}
        className="h-12 w-full rounded-xl border bg-background pl-11 pr-4 text-base shadow-xs"
      />
    </form>
  );
}
