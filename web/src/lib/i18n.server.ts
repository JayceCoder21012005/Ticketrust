import viServer from "../../messages/vi.server.json";
import { t as sharedT, type Locale } from "./i18n";

/**
 * Copy that only server components render (page text, FAQ, badges). It lives in `messages/vi.server.json` so it
 * never lands in the browser: `messages/vi.json` is bundled with every client component that calls `t()`, on
 * every page (170 KB budget). Falls back to the shared catalog, so server code can use this `t` for everything.
 * Client components must import `@/lib/i18n` instead (a test enforces it).
 */
const catalogs: Record<Locale, unknown> = { vi: viServer };

export function t(key: string, locale: Locale = "vi", values?: Record<string, string | number>): string {
  const found = key.split(".").reduce<unknown>(
    (node, part) => (typeof node === "object" && node !== null ? (node as Record<string, unknown>)[part] : undefined),
    catalogs[locale],
  );

  if (typeof found !== "string") {
    return sharedT(key, locale, values);
  }

  return values ? found.replace(/\{(\w+)\}/g, (match, name: string) => String(values[name] ?? match)) : found;
}
