import { api } from "@/lib/api/client";
import { ApiError, type ProblemDetails } from "@/lib/errors";
import type { NotificationPage } from "./types";

const fail = (status: number, error: unknown) => new ApiError(status, (error ?? {}) as ProblemDetails);

/** `GET /me/notifications`: newest first; pass the previous page's `nextCursor` to continue. */
export async function listNotifications(cursor?: string, limit?: number, signal?: AbortSignal): Promise<NotificationPage> {
  const result = await api.GET("/api/v1/me/notifications", { params: { query: { cursor, limit } }, signal, cache: "no-store" });
  if (result.data === undefined) {
    throw fail(result.response.status, result.error);
  }

  // The generated client types int32 as `number | string`.
  return { ...result.data, unreadCount: Number(result.data.unreadCount) };
}

/** `POST /me/notifications/{id}/read` (204). Idempotent: reading a read notification is fine. */
export async function markNotificationRead(id: string): Promise<void> {
  const result = await api.POST("/api/v1/me/notifications/{id}/read", { params: { path: { id } } });
  if (!result.response.ok) {
    throw fail(result.response.status, result.error);
  }
}

/** `POST /me/notifications/read-all` (204). */
export async function markAllNotificationsRead(): Promise<void> {
  const result = await api.POST("/api/v1/me/notifications/read-all");
  if (!result.response.ok) {
    throw fail(result.response.status, result.error);
  }
}
