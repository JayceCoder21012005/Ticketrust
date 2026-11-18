import type { components } from "@/lib/api/schema";

type Schemas = components["schemas"];

/** `upcoming`: the event has not ended, is not cancelled and the ticket is not voided; `past`: everything else. */
export type TicketScope = "upcoming" | "past";

export type TicketStatus = Schemas["TicketStatus"];

/** Seat labels; null for general admission. */
export type TicketSeat = Schemas["TicketSeat"];

export type TicketItem = Schemas["TicketItem"];

/** One event of the wallet with the user's tickets for it (`GET /me/tickets`). */
export type TicketEventGroup = Schemas["TicketEventGroup"];

export type TicketEventSummary = Schemas["TicketEventSummary"];

/** `GET /tickets/{id}`; `orderTicketIds` lists the order's tickets for swiping between them. */
export type TicketDetail = Schemas["TicketDetail"];

/**
 * `GET /tickets/{id}/qr-key`: the per-ticket HMAC secret (standard base64, 32 bytes) the wallet signs codes with.
 * A credential: cached only in the wallet's IndexedDB and dropped on logout.
 */
export type QrKey = { ticketId: string; secret: string; stepSeconds: number; format: string };

/** What a failed ticket request means to the wallet. Only `network` may fall back to a cached key. */
export type TicketFailure = "not_found" | "not_displayable" | "invalid_scope" | "rate_limited" | "busy" | "network" | "other";
