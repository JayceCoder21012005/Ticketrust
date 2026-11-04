import type { TierRemaining } from "@/lib/catalog/types";

/**
 * Seat shapes as the api serializes them (spec 07). Hand-written like the catalog types: the generated schema widens
 * int64 to `number | string`, and the api only ever sends numbers for these fields after `normaliseHold`.
 */
export type SeatState = "free" | "taken" | "mine" | "selected";

export type SeatInfo = { index: number; section: string; row: string; number: string; x: number; y: number; tierId: string };

export type SeatMapSection = { name: string; seatCount: number };

export type SeatMap = { eventId: string; version: string; sections: SeatMapSection[]; seats: SeatInfo[] };

/** Collapsed seat changes; `from..v` is the range of inventory versions it covers. */
export type SeatDelta = { from: number; v: number; held: number[]; freed: number[]; sold: number[]; tiers: TierRemaining[] };

export type HeldSeat = { index: number; section: string; row: string; number: string; tierId: string; price: number };

export type HeldGa = { tierId: string; name: string; quantity: number; price: number };

export type Hold = { holdId: string; seats: HeldSeat[]; ga: HeldGa[]; total: number; expiresAt: string; serverNow: string };

export type GaLine = { tierId: string; quantity: number };

export type CreateHoldBody = {
  seatIndexes?: number[];
  ga?: GaLine[];
  bestAvailable?: GaLine;
  replaceExisting: boolean;
};
