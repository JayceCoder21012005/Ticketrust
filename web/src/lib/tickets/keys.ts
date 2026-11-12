import type { TicketScope } from "./types";

/**
 * TanStack Query keys of the wallet. Everything lives under `["tickets"]`; the buyer area's client drops it all when the
 * user changes (`useDropCacheOnUserChange` in `AppProviders`). The QR key is never cached here: see `getQrKey`.
 */
export const ticketKeys = {
  all: ["tickets"] as const,
  list: (scope: TicketScope) => ["tickets", "list", scope] as const,
  detail: (id: string) => ["tickets", "detail", id] as const,
};
