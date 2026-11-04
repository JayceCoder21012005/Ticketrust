import { HubConnectionBuilder } from "@microsoft/signalr";
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
 * Live seat changes of one event (spec 07), opened only on the seat page. Reconnects on its own; every (re)join asks
 * the page to resync, because SignalR does not replay what was sent while the connection was down.
 */
export function connectSeatFeed(eventId: string, { accessToken, onDelta, onResync }: SeatFeedOptions): { stop: () => Promise<void> } {
  const connection = new HubConnectionBuilder()
    .withUrl(HUB_PATH, { accessTokenFactory: accessToken })
    .withAutomaticReconnect()
    .build();

  connection.on("SeatDelta", (delta: SeatDelta) => onDelta(delta));

  const join = async () => {
    await connection.invoke("JoinEvent", eventId);
    onResync();
  };

  connection.onreconnected(() => {
    void join().catch(() => undefined);
  });

  const started = connection
    .start()
    .then(join)
    .catch(() => undefined);

  return {
    async stop() {
      await started;
      await connection.stop();
    },
  };
}
