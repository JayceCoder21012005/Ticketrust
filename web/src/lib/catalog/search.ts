import type { components } from "@/lib/api/schema";

export type SearchSuggestions = components["schemas"]["SearchSuggestions"];
export type EventSuggestion = components["schemas"]["EventSuggestion"];

/** `GET /search/suggest?q=` through the same-origin rewrite (UI refresh). Short queries answer with what is hot. */
export async function fetchSuggestions(q: string, signal?: AbortSignal): Promise<SearchSuggestions> {
  const response = await fetch(`/api/v1/search/suggest?${new URLSearchParams({ q })}`, { signal, headers: { accept: "application/json" } });
  if (!response.ok) {
    throw new Error(`GET /api/v1/search/suggest failed with ${response.status}`);
  }

  return (await response.json()) as SearchSuggestions;
}

/** Lower case without accents (đ → d), one character in, one out, so offsets match the original text. */
export function foldChar(c: string): string {
  return c.normalize("NFD").replace(/[̀-ͯ]/g, "").replace(/đ/gi, "d").toLowerCase();
}
