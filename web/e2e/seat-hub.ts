import type { BrowserContext } from "@playwright/test";

const RS = "\u001e";

/** Minimal SignalR hub over long polling: handshake, invocation completions, and pushed SeatDelta messages. */
export async function mockHub(context: BrowserContext) {
  const outbox: string[] = [];
  const joined: string[] = [];
  await context.route("**/hubs/events/negotiate*", (route) =>
    route.fulfill({
      json: { connectionId: "c1", connectionToken: "t1", negotiateVersion: 1, availableTransports: [{ transport: "LongPolling", transferFormats: ["Text"] }] },
    }),
  );
  await context.route(/\/hubs\/events\?id=/, async (route) => {
    const method = route.request().method();
    if (method === "POST") {
      for (const frame of (route.request().postData() ?? "").split(RS).filter(Boolean)) {
        const message = JSON.parse(frame) as { type?: number; invocationId?: string; target?: string; arguments?: string[] };
        if (message.type === undefined) outbox.push(`{}${RS}`);
        if (message.target === "JoinEvent") joined.push(message.arguments![0]);
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
    joined,
    push: (delta: object) => outbox.push(`${JSON.stringify({ type: 1, target: "SeatDelta", arguments: [delta] })}${RS}`),
  };
}
