import viBuyer from "../../messages/vi.buyer.json";
import { lookup, t as sharedT, type Locale, type Values } from "./i18n";

/**
 * Copy of the buyer area (`tickets`, `checkout`, `queue`, `orders`, `seats`, `accountHub`, `waitlist`, `resale`) in `messages/vi.buyer.json`, kept out of
 * `messages/vi.json` so the public pages never download it. Only buyer code imports this `t` (a test checks both ways:
 * a file rendering a buyer key must import it, public code must not). Falls back to the shared catalog.
 */
const catalogs: Record<Locale, unknown> = { vi: viBuyer };

export function t(key: string, locale: Locale = "vi", values?: Values): string {
  return lookup(catalogs[locale], key, values) ?? sharedT(key, locale, values);
}
