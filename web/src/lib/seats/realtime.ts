import { connectHub } from "@/lib/realtime/hub-connection";
import type { SeatDelta } from "./types";

export const HUB_PATH = "/hubs/events";

export type SeatFeedOptions = {
  /** The page's access token; SignalR sends it as `access_token` on the hub URL. */
  accessToken: () => Promise<string>;
  onDelta: (delta: SeatDelta) => void;
  /** The feed (re)joined: whatever happened in between is unknown, so refetch availability. */
  onResync: () => void;
};

/**
 * Live seat changes of one event (spec 07), opened only on the seat page. Reconnect and backoff are the shared
 * `connectHub`. Every (re)join asks the page to resync, because SignalR does not replay what was sent while the
 * connection was down; a connection that cannot join the event's group is dropped and started again.
 */
export function connectSeatFeed(eventId: string, { accessToken, onDelta, onResync }: SeatFeedOptions): { stop: () => Promise<void> } {
  return connectHub({
    path: HUB_PATH,
    accessToken,
    subscribe: (connection) => connection.on("SeatDelta", (delta: SeatDelta) => onDelta(delta)),
    onConnected: async (connection) => {
      await connection.invoke("JoinEvent", eventId);
      onResync();
    },
  });
}
