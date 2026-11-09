/**
 * Order shapes as the api serializes them (spec 08). Hand-written like the seat types: the generated schema widens
 * int64 to `number | string`, and `normaliseOrder` turns those into numbers once at the edge.
 */
export type OrderStatus = "Pending" | "AwaitingPayment" | "PaymentFailed" | "Paid" | "Cancelled" | "Expired" | "RefundPending" | "Refunded";

export type OrderSeat = { index: number; section: string; row: string; number: string; tierId: string; price: number };

export type OrderGaLine = { tierId: string; name: string; quantity: number; price: number };

export type Buyer = { name: string; email: string; phone: string | null };

export type Order = {
  id: string;
  eventId: string;
  status: OrderStatus;
  seats: OrderSeat[];
  ga: OrderGaLine[];
  total: number;
  buyer: Buyer;
  expiresAt: string;
  serverNow: string;
  createdAt: string;
  paidAt: string | null;
  refundReason: string | null;
  ticketCount: number | null;
};

export type CreateOrderBody = { eventId: string; holdId: string; buyer: Buyer };

export type PayResult = { redirectUrl: string; attemptId: string; expiresAt: string; serverNow: string };

export type MockOutcome = "succeeded" | "failed" | "succeeded_delayed" | "succeeded_twice";

export type MockSession = {
  sessionId: string;
  orderId: string;
  eventId: string;
  label: string;
  amount: number;
  currency: string;
  status: "Pending" | "Succeeded" | "Failed" | "Expired";
  expiresAt: string;
  serverNow: string;
};
