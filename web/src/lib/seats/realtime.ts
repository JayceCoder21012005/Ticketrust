import { HubConnectionBuilder } from "@microsoft/signalr";
import type { SeatDelta } from "./types";

export const HUB_PATH = "/hubs/events";

/** 1 s, 2 s, 4 s … capped at 30 s, forever: a laptop waking up or an api restart must not end the live map. */
export const retryDelayMs = (attempt: number) => Math.min(30_000, 1_000 * 2 ** Math.min(attempt, 5));

export type SeatFeedOptions = {
  /** The page's access token; SignalR sends it as `access_token` on the hub URL. */
  accessToken: () => Promise<string>;
  onDelta: (delta: SeatDelta) => void;
  /** The feed (re)joined: whatever happened in between is unknown, so refetch availability. */
  onResync: () => void;
};

/**
 * Live seat changes of one event (spec 07), opened only on the seat page. It never gives up: automatic reconnect keeps
 * retrying with capped backoff, and a connection that closes anyway (or never started) is started again. Every
 * (re)join asks the page to resync, because SignalR does not replay what was sent while the connection was down.
 */
export function connectSeatFeed(eventId: string, { accessToken, onDelta, onResync }: SeatFeedOptions): { stop: () => Promise<void> } {
  const connection = new HubConnectionBuilder()
    .withUrl(HUB_PATH, { accessTokenFactory: accessToken })
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (context) => retryDelayMs(context.previousRetryCount) })
    .build();

  let stopped = false;
  let attempt = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const join = async () => {
    await connection.invoke("JoinEvent", eventId);
    onResync();
  };

  // A connection that is up but never joined the group would sit silent for good (and cannot be start()ed again):
  // close it, and onclose below schedules the next attempt.
  const leave = () => void connection.stop().catch(() => undefined);

  const start = async () => {
    try {
      await connection.start();
    } catch {
      if (!stopped) timer = setTimeout(() => void start(), retryDelayMs(attempt++));
      return;
    }

    attempt = 0;
    await join().catch(leave);
  };

  connection.on("SeatDelta", (delta: SeatDelta) => onDelta(delta));
  connection.onreconnected(() => {
    void join().catch(leave);
  });
  connection.onclose(() => {
    if (!stopped) timer = setTimeout(() => void start(), retryDelayMs(attempt++));
  });

  const started = start();

  return {
    async stop() {
      stopped = true;
      clearTimeout(timer);
      await started;
      await connection.stop();
    },
  };
}
