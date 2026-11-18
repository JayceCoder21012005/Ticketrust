"use client";

import { useEffect, useRef, useState } from "react";
import { fetchSuggestions, type SearchSuggestions } from "@/lib/catalog/search";

/** Short enough to feel instant, long enough that fast typing sends one request, not one per key. */
export const SUGGEST_DEBOUNCE_MS = 120;

/**
 * Suggestions for what is typed (UI refresh). Debounced, the previous request is aborted, and an answer is only applied when it
 * belongs to the latest query: a slow early answer can never replace a later one. The last results stay while the next load.
 */
export function useSuggestions(query: string): { data: SearchSuggestions | null; loading: boolean } {
  const [data, setData] = useState<SearchSuggestions | null>(null);
  const [loading, setLoading] = useState(false);
  const latest = useRef(0);

  useEffect(() => {
    const id = ++latest.current;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      setLoading(true);
      fetchSuggestions(query.trim(), controller.signal).then(
        (answer) => {
          if (id !== latest.current) return;
          setData(answer);
          setLoading(false);
        },
        () => {
          if (id === latest.current) setLoading(false);
        },
      );
    }, SUGGEST_DEBOUNCE_MS);

    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [query]);

  return { data, loading };
}
