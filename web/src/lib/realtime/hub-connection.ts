import { HubConnectionBuilder, type HubConnection } from "@microsoft/signalr";

/** 1 s, 2 s, 4 s … capped at 30 s, forever: a laptop waking up or an api restart must not end a live feed. */
export const retryDelayMs = (attempt: number) => Math.min(30_000, 1_000 * 2 ** Math.min(attempt, 5));

export type HubOptions = {
  path: string;
  /** The page's access token; SignalR sends it as `access_token` on the hub URL. */
  accessToken: () => Promise<string>;
  /** Registers the server-to-client handlers, before the connection starts. */
  subscribe: (connection: HubConnection) => void;
  /**
   * Runs after every (re)connect: join a group, ask the page to resync. If it rejects, the connection is dropped and
   * started again with backoff, because a connection that never finished its setup would sit silent for good.
   */
  onConnected: (connection: HubConnection) => Promise<void>;
};

/**
 * One SignalR connection that never gives up: automatic reconnect keeps retrying with capped backoff, and a connection
 * that closes anyway (or never started) is started again. Shared by the seat feed (spec 07) and the user feed (spec 09).
 */
export function connectHub({ path, accessToken, subscribe, onConnected }: HubOptions): { stop: () => Promise<void> } {
  const connection = new HubConnectionBuilder()
    .withUrl(path, { accessTokenFactory: accessToken })
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (context) => retryDelayMs(context.previousRetryCount) })
    .build();

  let stopped = false;
  let attempt = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;

  // A connection that is up but never finished onConnected cannot be start()ed again: close it,
  // and onclose below schedules the next attempt.
  const leave = () => void connection.stop().catch(() => undefined);

  // The backoff restarts only once onConnected worked; resetting it earlier would retry a failing setup every second forever.
  const connected = () =>
    onConnected(connection).then(() => {
      attempt = 0;
    }, leave);

  const start = async () => {
    try {
      await connection.start();
    } catch {
      if (!stopped) timer = setTimeout(() => void start(), retryDelayMs(attempt++));
      return;
    }

    await connected();
  };

  subscribe(connection);
  connection.onreconnected(() => {
    void connected();
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
