import { connectHub } from "@/lib/realtime/hub-connection";
import type { NotificationItem } from "./types";

export const USER_HUB_PATH = "/hubs/user";

export type UserFeedOptions = {
  /** The page's access token; SignalR sends it as `access_token` on the hub URL. */
  accessToken: () => Promise<string>;
  onNotification: (item: NotificationItem) => void;
  onOrderStatus: (orderId: string, status: string) => void;
  /**
   * The feed (re)connected: pushes sent while it was down are lost, and the server adds the connection to the user's
   * group only just after the handshake, so a push in the first milliseconds can miss it too. Refetch the inbox.
   */
  onResync: () => void;
};

/**
 * The signed-in user's live messages (spec 09): `Notification` and `OrderStatusChanged`. There is nothing to join: the
 * server puts the connection in the user's group itself, clients never choose one. Same reconnect and backoff as
 * `connectSeatFeed` (the shared `connectHub`); every (re)connect asks for a resync.
 */
export function connectUserFeed({ accessToken, onNotification, onOrderStatus, onResync }: UserFeedOptions): { stop: () => Promise<void> } {
  return connectHub({
    path: USER_HUB_PATH,
    accessToken,
    subscribe: (connection) => {
      connection.on("Notification", (item: NotificationItem) => onNotification(item));
      connection.on("OrderStatusChanged", (orderId: string, status: string) => onOrderStatus(orderId, status));
    },
    onConnected: async () => onResync(),
  });
}
