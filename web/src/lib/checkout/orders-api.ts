import { api } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { ApiError, type ProblemDetails } from "@/lib/errors";
import { currentPurchaseTrace, traceHeader } from "./purchase-trace";
import type { CreateOrderBody, MockOutcome, MockSession, Order, OrderPage, PayResult } from "./types";

type RawOrder = components["schemas"]["OrderResponse"];
type RawSession = components["schemas"]["MockSessionView"];

const fail = (status: number, error: unknown) => new ApiError(status, (error ?? {}) as ProblemDetails);

/** The generated client types int64 as `number | string`; the pages work with numbers. */
export function normaliseOrder(raw: RawOrder): Order {
  return {
    ...raw,
    total: Number(raw.total),
    seats: raw.seats.map((s) => ({ ...s, index: Number(s.index), price: Number(s.price) })),
    ga: raw.ga.map((g) => ({ ...g, quantity: Number(g.quantity), price: Number(g.price) })),
    ticketCount: raw.ticketCount === null ? null : Number(raw.ticketCount),
  };
}

/** `GET /me/orders`: newest first; pass the previous page's `nextCursor` to continue. */
export async function listMyOrders(cursor?: string, limit?: number, signal?: AbortSignal): Promise<OrderPage> {
  const result = await api.GET("/api/v1/me/orders", { params: { query: { cursor, limit } }, signal, cache: "no-store" });
  if (result.data === undefined) {
    throw fail(result.response.status, result.error);
  }

  return {
    nextCursor: result.data.nextCursor,
    items: result.data.items.map((item) => ({ ...item, total: Number(item.total), itemCount: Number(item.itemCount) })),
  };
}

const normaliseSession = (raw: RawSession): MockSession => ({ ...raw, amount: Number(raw.amount) });

/**
 * `POST /orders`: 201 for a new order, 200 when the hold already has an open one. Both are an order, so the caller
 * only goes on to `/checkout/{id}`. Reuse `idempotencyKey` when retrying the same request (C2).
 */
export async function createOrder(body: CreateOrderBody, idempotencyKey: string): Promise<Order> {
  const result = await api.POST("/api/v1/orders", { headers: { "Idempotency-Key": idempotencyKey, ...traceHeader(currentPurchaseTrace()) }, body });
  if (result.data === undefined) {
    throw fail(result.response.status, result.error);
  }

  return normaliseOrder(result.data);
}

export async function getOrder(id: string, signal?: AbortSignal): Promise<Order> {
  const result = await api.GET("/api/v1/orders/{id}", { params: { path: { id } }, signal, cache: "no-store" });
  if (result.data === undefined) {
    throw fail(result.response.status, result.error);
  }

  return normaliseOrder(result.data);
}

/** `POST /orders/{id}/pay`: where to send the buyer. The caller owns the Idempotency-Key policy (see idempotency.ts). */
export async function payOrder(id: string, idempotencyKey: string): Promise<PayResult> {
  const result = await api.POST("/api/v1/orders/{id}/pay", {
    params: { path: { id } },
    headers: { "Idempotency-Key": idempotencyKey, ...traceHeader(currentPurchaseTrace()) },
  });
  if (result.data === undefined) {
    throw fail(result.response.status, result.error);
  }

  return result.data;
}

/** `POST /orders/{id}/cancel`: only a `Pending` order can be cancelled (else 409 `order.not_payable`). */
export async function cancelOrder(id: string): Promise<Order> {
  const result = await api.POST("/api/v1/orders/{id}/cancel", { params: { path: { id } }, headers: { "Idempotency-Key": crypto.randomUUID() } });
  if (result.data === undefined) {
    throw fail(result.response.status, result.error);
  }

  return normaliseOrder(result.data);
}

/** Dev/demo only (`Payments:Mock:Enabled`): a production api answers 404 for both calls below. */
export async function getMockSession(sessionId: string): Promise<MockSession> {
  const result = await api.GET("/api/v1/mock-gateway/sessions/{sessionId}", { params: { path: { sessionId } }, cache: "no-store" });
  if (result.data === undefined) {
    throw fail(result.response.status, result.error);
  }

  return normaliseSession(result.data);
}

export async function completeMockSession(sessionId: string, outcome: MockOutcome): Promise<MockSession> {
  const result = await api.POST("/api/v1/mock-gateway/sessions/{sessionId}/complete", { params: { path: { sessionId } }, body: { outcome } });
  if (result.data === undefined) {
    throw fail(result.response.status, result.error);
  }

  return normaliseSession(result.data);
}
