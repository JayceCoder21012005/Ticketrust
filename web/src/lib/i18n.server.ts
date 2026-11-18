import viServer from "../../messages/vi.server.json";
import { lookup, type Locale, type Values } from "./i18n";
import { t as buyerT } from "./i18n.buyer";

/**
 * Copy that only server components render (page text, FAQ, badges). It lives in `messages/vi.server.json` so it
 * never lands in the browser: `messages/vi.json` is bundled with every client component that calls `t()`, on
 * every page (170 KB budget). Falls back to the buyer catalog and then the shared one, so server code can use this
 * `t` for everything. Client components must import `@/lib/i18n` (or `@/lib/i18n.buyer`) instead (a test enforces it).
 */
const catalogs: Record<Locale, unknown> = { vi: viServer };

export function t(key: string, locale: Locale = "vi", values?: Values): string {
  return lookup(catalogs[locale], key, values) ?? buyerT(key, locale, values);
}
