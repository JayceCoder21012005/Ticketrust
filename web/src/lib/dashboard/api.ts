import { api } from "@/lib/api/client";
import type { components } from "@/lib/api/schema";
import { getAccessToken, refreshAccessToken } from "@/lib/api/session";
import { ApiError, type ProblemDetails } from "@/lib/errors";
import { connectHub } from "@/lib/realtime/hub-connection";

type Schemas = components["schemas"];

/** The api's int64 fields arrive as numbers (the schema also allows numeric strings). */
type Num<T> = { [K in keyof T]: T[K] extends string ? T[K] : T[K] extends number | string ? number : T[K] };

export type DashboardTier = Num<Schemas["DashboardTier"]>;
export type DashboardFunnel = Num<Schemas["DashboardFunnel"]>;
export type DashboardQueue = Num<Schemas["DashboardQueue"]>;
export type RecentOrder = Num<Schemas["RecentOrder"]>;
export type DashboardSnapshot = Omit<Num<Schemas["DashboardSnapshot"]>, "tiers" | "funnel" | "queue" | "recentOrders"> & {
  tiers: DashboardTier[];
  funnel: DashboardFunnel;
  queue: DashboardQueue;
  recentOrders: RecentOrder[];
};
export type SalesPoint = Num<Schemas["SalesPoint"]>;
export type SalesSeries = Omit<Schemas["SalesSeries"], "points"> & { points: SalesPoint[] };
export type Bucket = "1m" | "1h";

const fail = (status: number, error: unknown) => new ApiError(status, (error ?? {}) as ProblemDetails);
const byId = (id: string) => ({ params: { path: { id } } });

/** `GET /organizer/events/{id}/dashboard`: the snapshot the page renders before the live feed connects. */
export async function getDashboard(eventId: string, signal?: AbortSignal): Promise<DashboardSnapshot> {
  const result = await api.GET("/api/v1/organizer/events/{id}/dashboard", { ...byId(eventId), signal, cache: "no-store" });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data as unknown as DashboardSnapshot;
}

/** `GET /organizer/events/{id}/sales-series`: per tier and minute (last 2 h) or hour (last 7 days). */
export async function getSalesSeries(eventId: string, bucket: Bucket, signal?: AbortSignal): Promise<SalesSeries> {
  const result = await api.GET("/api/v1/organizer/events/{id}/sales-series", {
    params: { path: { id: eventId }, query: { bucket } },
    signal,
    cache: "no-store",
  });
  if (result.data === undefined) throw fail(result.response.status, result.error);
  return result.data as unknown as SalesSeries;
}

/**
 * `GET /organizer/events/{id}/export.csv`, saved as a file. The route needs the bearer token, so a plain link cannot fetch it;
 * the server streams the rows and the browser keeps them as one download.
 */
export async function downloadAttendees(eventId: string): Promise<void> {
  const token = getAccessToken() ?? (await refreshAccessToken());
  const response = await fetch(`/api/v1/organizer/events/${eventId}/export.csv`, { headers: token ? { authorization: `Bearer ${token}` } : {} });
  if (!response.ok) throw new ApiError(response.status, {} as ProblemDetails);
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement("a");
  link.href = url;
  link.download = `attendees-${eventId}.csv`;
  link.click();
  URL.revokeObjectURL(url);
}

/** How often the page tells the hub it is still watching (the Worker forgets a viewer after 90 s of silence). */
export const HEARTBEAT_MS = 30_000;

export type DashboardFeedOptions = {
  onSnapshot: (snapshot: DashboardSnapshot) => void;
  onStatus: (live: boolean) => void;
};

/**
 * The live feed of `/hubs/organizer` (spec 15): joins the event's group on every (re)connect, repeats the join as a heartbeat
 * and reports whether the feed is up. One snapshot per second arrives while the event is on sale.
 */
export function connectDashboardFeed(eventId: string, { onSnapshot, onStatus }: DashboardFeedOptions): { stop: () => Promise<void> } {
  let beat: ReturnType<typeof setInterval> | undefined;
  const feed = connectHub({
    path: "/hubs/organizer",
    accessToken: async () => getAccessToken() ?? (await refreshAccessToken()) ?? "",
    subscribe: (connection) => {
      connection.on("DashboardSnapshot", (snapshot: DashboardSnapshot) => onSnapshot(snapshot));
      connection.onreconnecting(() => onStatus(false));
      connection.onclose(() => onStatus(false));
    },
    onConnected: async (connection) => {
      await connection.invoke("JoinDashboard", eventId);
      onStatus(true);
      clearInterval(beat);
      beat = setInterval(() => void connection.invoke("JoinDashboard", eventId).catch(() => onStatus(false)), HEARTBEAT_MS);
    },
  });

  return {
    async stop() {
      clearInterval(beat);
      await feed.stop();
    },
  };
}
