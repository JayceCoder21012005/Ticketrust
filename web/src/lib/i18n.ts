import vi from "../../messages/vi.json";

/**
 * Only Vietnamese is served: a catalog imported here ships with every client component that calls t(),
 * so English (messages/en.json, kept key-for-key in sync by a test) is added when a language switch exists.
 */
export type Locale = "vi";

const catalogs: Record<Locale, unknown> = { vi };

/**
 * Looks up copy by dotted key (`common.retry`). Vietnamese is the UI language.
 * A missing key renders as the key itself so gaps are obvious in review instead of silently blank.
 */
export function t(key: string, locale: Locale = "vi", values?: Record<string, string | number>): string {
  const found = key.split(".").reduce<unknown>(
    (node, part) => (typeof node === "object" && node !== null ? (node as Record<string, unknown>)[part] : undefined),
    catalogs[locale],
  );

  if (typeof found !== "string") {
    return key;
  }

  return values ? found.replace(/\{(\w+)\}/g, (match, name: string) => String(values[name] ?? match)) : found;
}
