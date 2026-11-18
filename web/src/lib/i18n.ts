import vi from "../../messages/vi.json";

/**
 * Only Vietnamese is served: a catalog imported here ships with every client component that calls t(),
 * so English (messages/en.json, kept key-for-key in sync by a test) is added when a language switch exists.
 *
 * `messages/vi.json` is bundled with every page's client code (public budget: 170 KB, see `lighthouserc.json`), so it
 * holds only copy the public pages and the shared chrome use. Copy for one area lives in its own catalog, reached
 * through that area's `t`: `i18n.server.ts` (server components only, never the browser) and `i18n.buyer.ts` (the buyer
 * area: tickets, checkout, queue, orders, seats, whose client components import `@/lib/i18n.buyer`). A key lives in
 * exactly one catalog; the area's `t` falls back to this one, so it works for `common.*` and the rest too.
 */
export type Locale = "vi";

export type Values = Record<string, string | number>;

const catalogs: Record<Locale, unknown> = { vi };

/** The text at the dotted `key` in `catalog`, with `{placeholders}` filled from `values`; null when there is none. */
export function lookup(catalog: unknown, key: string, values?: Values): string | null {
  const found = key.split(".").reduce<unknown>(
    (node, part) => (typeof node === "object" && node !== null ? (node as Record<string, unknown>)[part] : undefined),
    catalog,
  );

  if (typeof found !== "string") {
    return null;
  }

  return values ? found.replace(/\{(\w+)\}/g, (match, name: string) => String(values[name] ?? match)) : found;
}

/**
 * Looks up copy by dotted key (`common.retry`). Vietnamese is the UI language.
 * A missing key renders as the key itself so gaps are obvious in review instead of silently blank.
 */
export function t(key: string, locale: Locale = "vi", values?: Values): string {
  return lookup(catalogs[locale], key, values) ?? key;
}
