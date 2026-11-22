import { api } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { ApiError, type ProblemDetails } from "@/lib/errors";

type Schemas = components["schemas"];

export type AdminUser = Schemas["AdminUser"];
export type AdminUserDetail = Schemas["AdminUserDetail"];
export type AdminOrderRow = Omit<Schemas["AdminOrderRow"], "total" | "tickets"> & { total: number; tickets: number };
export type AdminOrderDetail = Omit<Schemas["AdminOrderDetail"], "total"> & { total: number };
export type AdminEventRow = Schemas["AdminEventRow"];
export type OrganizerApplication = Schemas["OrganizerApplicationView"];
export type DeadLetter = Omit<Schemas["DeadLetter"], "attempts"> & { attempts: number };
export type AdminFlag = Schemas["AdminFlagRow"];
export type AuditRow = Schemas["AuditRow"];
export type Page<T> = { items: T[]; nextCursor: string | null };

const fail = (status: number, error: unknown) => new ApiError(status, (error ?? {}) as ProblemDetails);

function data<T>(result: { data?: unknown; error?: unknown; response: Response }): T {
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data as T;
}

function done(result: { error?: unknown; response: Response }): void {
  if (!result.response.ok) throw fail(result.response.status, result.error);
}

const id = (value: string) => ({ path: { id: value } });
const list = (q: Record<string, string | undefined>) => Object.fromEntries(Object.entries(q).filter(([, v]) => v));

// ── Users ──
export const searchUsers = async (q: string | undefined, cursor: string | null) =>
  data<Page<AdminUser>>(await api.GET("/api/v1/admin/users", { params: { query: list({ q, cursor: cursor ?? undefined }) }, cache: "no-store" }));
export const getUser = async (userId: string) => data<AdminUserDetail>(await api.GET("/api/v1/admin/users/{id}", { params: id(userId), cache: "no-store" }));
export const lockUser = async (userId: string, reason: string) => data<AdminUser>(await api.POST("/api/v1/admin/users/{id}/lock", { params: id(userId), body: { reason } }));
export const unlockUser = async (userId: string, reason: string) => data<AdminUser>(await api.POST("/api/v1/admin/users/{id}/unlock", { params: id(userId), body: { reason } }));
export const grantRole = async (userId: string, role: string, reason: string) =>
  data<AdminUser>(await api.POST("/api/v1/admin/users/{id}/roles", { params: id(userId), body: { role, reason } }));
export const revokeRole = async (userId: string, role: string, reason: string) =>
  data<AdminUser>(await api.POST("/api/v1/admin/users/{id}/roles/revoke", { params: id(userId), body: { role, reason } }));

// ── Organizer applications ──
export const listApplications = async (status: string | undefined, cursor: string | null) =>
  data<Page<OrganizerApplication>>(
    await api.GET("/api/v1/admin/organizer-applications", {
      params: { query: list({ status, cursor: cursor ?? undefined }) as { status?: Schemas["OrganizerApplicationStatus"]; cursor?: string } },
      cache: "no-store",
    }),
  );
export const decideApplication = async (applicationId: string, approve: boolean, reason: string) =>
  data<OrganizerApplication>(
    approve
      ? await api.POST("/api/v1/admin/organizer-applications/{id}/approve", { params: id(applicationId), body: { reason } })
      : await api.POST("/api/v1/admin/organizer-applications/{id}/reject", { params: id(applicationId), body: { reason } }),
  );

// ── Events ──
export const listEvents = async (q: string | undefined, status: string | undefined, cursor: string | null) =>
  data<Page<AdminEventRow>>(
    await api.GET("/api/v1/admin/events", {
      params: { query: list({ q, status, cursor: cursor ?? undefined }) as { q?: string; status?: Schemas["EventStatus"]; cursor?: string } },
      cache: "no-store",
    }),
  );
export const moderateEvent = async (eventId: string, suspend: boolean, reason: string) =>
  data<AdminEventRow>(
    suspend
      ? await api.POST("/api/v1/admin/events/{id}/suspend", { params: id(eventId), body: { reason } })
      : await api.POST("/api/v1/admin/events/{id}/restore", { params: id(eventId), body: { reason } }),
  );

// ── Orders ──
export const searchOrders = async (q: string | undefined, cursor: string | null) =>
  data<Page<AdminOrderRow>>(await api.GET("/api/v1/admin/orders", { params: { query: list({ q, cursor: cursor ?? undefined }) }, cache: "no-store" }));
export const getOrder = async (orderId: string) => data<AdminOrderDetail>(await api.GET("/api/v1/admin/orders/{id}", { params: id(orderId), cache: "no-store" }));
export const refundOrder = async (orderId: string, reason: string, confirm: string) =>
  data<AdminOrderDetail>(await api.POST("/api/v1/admin/orders/{id}/refund", { params: id(orderId), body: { reason, confirm } }));

// ── Outbox ──
export const listDeadLetters = async (cursor: string | null) =>
  data<Page<DeadLetter>>(await api.GET("/api/v1/admin/outbox/dead-letters", { params: { query: list({ cursor: cursor ?? undefined }) }, cache: "no-store" }));
export const resolveDeadLetter = async (messageId: string, retry: boolean, reason: string) =>
  done(
    retry
      ? await api.POST("/api/v1/admin/outbox/{id}/retry", { params: id(messageId), body: { reason } })
      : await api.POST("/api/v1/admin/outbox/{id}/discard", { params: id(messageId), body: { reason } }),
  );

// ── Risk flags ──
export const listFlags = async (open: boolean, cursor: string | null) =>
  data<Page<AdminFlag>>(await api.GET("/api/v1/admin/flags", { params: { query: { open, ...list({ cursor: cursor ?? undefined }) } }, cache: "no-store" }));
export const resolveFlag = async (flagId: string, lock: boolean, reason: string) =>
  data<AdminFlag>(
    lock
      ? await api.POST("/api/v1/admin/flags/{id}/lock", { params: id(flagId), body: { reason } })
      : await api.POST("/api/v1/admin/flags/{id}/dismiss", { params: id(flagId), body: { reason } }),
  );

// ── Audit ──
export type AuditFilters = { actor?: string; entityType?: string; entityId?: string; action?: string; from?: string; to?: string };
export const listAudit = async (filters: AuditFilters, cursor: string | null) =>
  data<Page<AuditRow>>(await api.GET("/api/v1/admin/audit", { params: { query: list({ ...filters, cursor: cursor ?? undefined }) }, cache: "no-store" }));
