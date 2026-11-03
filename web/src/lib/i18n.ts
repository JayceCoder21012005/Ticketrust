import en from "../../messages/en.json";
import vi from "../../messages/vi.json";

export type Locale = "vi" | "en";

const catalogs: Record<Locale, unknown> = { vi, en };

/**
 * Looks up copy by dotted key (`common.retry`). Vietnamese is the UI language; English is secondary.
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
