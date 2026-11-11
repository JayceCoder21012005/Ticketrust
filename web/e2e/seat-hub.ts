import type { BrowserContext } from "@playwright/test";

const RS = "\u001e";

export type Invocation = { target: string; arguments: unknown[] };

/**
 * Minimal SignalR hub over long polling at `/hubs/<name>`: handshake, invocation completions and server-to-client
 * messages pushed with `send`. The page's real @microsoft/signalr client talks to it.
 */
export async function mockSignalRHub(context: BrowserContext, name: string) {
  const outbox: string[] = [];
  const invocations: Invocation[] = [];
  const state = { connections: 0, handshakes: 0 };
  await context.route(`**/hubs/${name}/negotiate*`, (route) => {
    state.connections++;
    return route.fulfill({
      json: { connectionId: "c1", connectionToken: "t1", negotiateVersion: 1, availableTransports: [{ transport: "LongPolling", transferFormats: ["Text"] }] },
    });
  });
  await context.route(new RegExp(`/hubs/${name}\\?id=`), async (route) => {
    const method = route.request().method();
    if (method === "POST") {
      for (const frame of (route.request().postData() ?? "").split(RS).filter(Boolean)) {
        const message = JSON.parse(frame) as { type?: number; invocationId?: string; target?: string; arguments?: unknown[] };
        if (message.type === undefined) {
          state.handshakes++;
          outbox.push(`{}${RS}`);
        }
        if (message.target) invocations.push({ target: message.target, arguments: message.arguments ?? [] });
        if (message.invocationId) outbox.push(`${JSON.stringify({ type: 3, invocationId: message.invocationId, result: null })}${RS}`);
      }

      return route.fulfill({ status: 200, body: "" });
    }

    if (method === "DELETE") return route.fulfill({ status: 202, body: "" });
    for (let waited = 0; outbox.length === 0 && waited < 1_000; waited += 50) {
      await new Promise((resolve) => setTimeout(resolve, 50));
    }

    return route.fulfill({ status: 200, contentType: "text/plain", body: outbox.splice(0).join("") });
  });
  return {
    state,
    invocations,
    send: (target: string, ...args: unknown[]) => outbox.push(`${JSON.stringify({ type: 1, target, arguments: args })}${RS}`),
  };
}

/** The seat hub (spec 07): the event groups the page joined, and `push` for a SeatDelta. */
export async function mockHub(context: BrowserContext) {
  const hub = await mockSignalRHub(context, "events");
  return {
    get joined() {
      return hub.invocations.filter((call) => call.target === "JoinEvent").map((call) => String(call.arguments[0]));
    },
    push: (delta: object) => hub.send("SeatDelta", delta),
  };
}
