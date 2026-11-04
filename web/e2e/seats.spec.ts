import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";

/**
 * The seat page against a scripted api and a scripted SignalR hub (spec 07): the .NET integration tests cover the real
 * ones. The hub mock speaks SignalR's long-polling transport, so the page's real @microsoft/signalr client is used.
 */
const SLUG = "son-tung-mtp-live";
const MIN = 60_000;
const RS = "\u001e";

const user = {
  accessToken: "e2e-buyer-token",
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

type EventInfo = { id: string; tiers: { id: string; kind: string }[] };

async function eventInfo(page: Page, baseURL: string): Promise<EventInfo> {
  const response = await page.request.get(`${baseURL}/api/v1/events/${SLUG}`);
  return (await response.json()) as EventInfo;
}

/** Two rows of six seats of the first seated tier, one section: the map opens straight into the seats. */
function seatMap(event: EventInfo) {
  const tier = event.tiers.find((t) => t.kind === "Seated")!.id;
  const seats = Array.from({ length: 12 }, (_, i) => ({
    index: i,
    section: "A",
    row: String(Math.floor(i / 6) + 1),
    number: String((i % 6) + 1),
    x: 380 + (i % 6) * 24,
    y: 200 + Math.floor(i / 6) * 28,
    tierId: tier,
  }));
  return { eventId: event.id, version: "e2e", sections: [{ name: "A", seatCount: 12 }], seats };
}

function holdOf(indexes: number[], tierId: string) {
  return {
    holdId: "h1",
    seats: indexes.map((i) => ({ index: i, section: "A", row: String(Math.floor(i / 6) + 1), number: String((i % 6) + 1), tierId, price: 1_500_000 })),
    ga: [],
    total: indexes.length * 1_500_000,
    expiresAt: new Date(Date.now() + 8 * MIN).toISOString(),
    serverNow: new Date().toISOString(),
  };
}

/** Minimal SignalR hub over long polling: handshake, invocation completions, and pushed SeatDelta messages. */
async function mockHub(context: BrowserContext) {
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

async function arrange(context: BrowserContext, page: Page, baseURL: string, { admitted = true } = {}) {
  const event = await eventInfo(page, baseURL);
  const map = seatMap(event);
  const tierId = map.seats[0].tierId;
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await context.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: user }));
  if (admitted) {
    await context.addInitScript(
      (admission) => localStorage.setItem("tr.admission", JSON.stringify(admission)),
      { eventId: event.id, slug: SLUG, token: "adm-token", expiresAt: new Date(Date.now() + 10 * MIN).toISOString() },
    );
  }

  const state = { holds: [] as { body: { seatIndexes: number[] }; headers: Record<string, string> }[] };
  await context.route(`**/api/v1/events/${event.id}/seat-map`, (route) => route.fulfill({ json: map }));
  await context.route(`**/api/v1/events/${event.id}/availability`, (route) =>
    route.fulfill({ json: { serverNow: new Date().toISOString(), version: 1, unavailable: [11], tiers: [] } }),
  );
  await context.route(`**/api/v1/events/${event.id}/holds/me`, (route) => route.fulfill({ status: 404, json: { code: "hold.not_found" } }));
  await context.route(`**/api/v1/events/${event.id}/holds`, (route) => {
    const body = route.request().postDataJSON() as { seatIndexes: number[] };
    state.holds.push({ body, headers: route.request().headers() });
    return route.fulfill({ json: holdOf(body.seatIndexes, tierId) });
  });
  const hub = await mockHub(context);
  return { event, hub, state };
}

const seat = (page: Page, row: number, number: number) => page.getByRole("button", { name: new RegExp(`^Khu A, hàng ${row}, ghế ${number},`) });

test("guarded: a signed-out visitor is sent to login and comes back to the seats", async ({ page }) => {
  await page.goto(`/events/${SLUG}/seats`);

  await expect(page).toHaveURL(new RegExp(`/login\\?returnUrl=%2Fevents%2F${SLUG}%2Fseats`));
});

test("without an admission the buyer is sent to the waiting room", async ({ context, page, baseURL }) => {
  await arrange(context, page, baseURL!, { admitted: false });
  await context.route("**/api/v1/events/*/queue/**", (route) => route.fulfill({ status: 503, json: { code: "system.busy" } }));

  await page.goto(`/events/${SLUG}/seats`);

  await expect(page).toHaveURL(new RegExp(`/events/${SLUG}/queue$`));
});

test("holds two seats and shows the hold countdown", async ({ context, page, baseURL }) => {
  const { state } = await arrange(context, page, baseURL!);
  await page.goto(`/events/${SLUG}/seats`);

  await seat(page, 1, 3).click();
  await seat(page, 1, 4).click();
  await page.getByRole("button", { name: "Tiếp tục" }).first().click();

  await expect(page.getByRole("timer").filter({ hasText: "Giữ chỗ còn" })).toContainText(/0[78]:\d\d/);
  expect(state.holds).toHaveLength(1);
  expect(state.holds[0].body.seatIndexes).toEqual([2, 3]);
  expect(state.holds[0].headers["x-admission-token"]).toBe("adm-token");
  expect(state.holds[0].headers["idempotency-key"]).toMatch(/^[0-9a-f-]{36}$/);
  await expect(seat(page, 1, 3)).toHaveAccessibleName(/bạn đang giữ$/);
});

test("a seat another buyer takes turns unavailable without a reload", async ({ context, page, baseURL }) => {
  const { event, hub } = await arrange(context, page, baseURL!);
  await page.goto(`/events/${SLUG}/seats`);
  await expect(seat(page, 1, 2)).toHaveAccessibleName(/còn trống$/);
  await expect.poll(() => hub.joined).toContain(event.id);

  hub.push({ from: 2, v: 2, held: [1], freed: [11], sold: [], tiers: [] });

  await expect(seat(page, 1, 2)).toHaveAccessibleName(/đã có người chọn$/, { timeout: 2_000 });
  await expect(seat(page, 2, 6)).toHaveAccessibleName(/còn trống$/);
});

test("keyboard-only buyers can pick seats and continue", async ({ context, page, baseURL }) => {
  const { state } = await arrange(context, page, baseURL!);
  await page.goto(`/events/${SLUG}/seats`);
  await expect(seat(page, 1, 1)).toBeVisible();

  for (let i = 0; i < 40; i++) {
    if (await page.evaluate(() => document.activeElement?.hasAttribute("data-i") ?? false)) break;
    await page.keyboard.press("Tab");
  }

  await expect(seat(page, 1, 1)).toBeFocused();
  await page.keyboard.press("ArrowRight");
  await expect(seat(page, 1, 2)).toBeFocused();
  await page.keyboard.press("Enter");
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("Space");
  await expect(seat(page, 2, 2)).toHaveAccessibleName(/đang chọn$/);

  const continueButton = page.getByRole("button", { name: "Tiếp tục" }).first();
  for (let i = 0; i < 40; i++) {
    if (await continueButton.evaluate((el) => el === document.activeElement)) break;
    await page.keyboard.press("Tab");
  }

  await page.keyboard.press("Enter");
  await expect.poll(() => state.holds.length).toBe(1);
  expect(state.holds[0].body.seatIndexes).toEqual([1, 7]);
});

for (const colorScheme of ["light", "dark"] as const) {
  test(`the seat page has no serious accessibility violations (${colorScheme})`, async ({ context, page, baseURL }) => {
    await page.emulateMedia({ colorScheme });
    await arrange(context, page, baseURL!);
    await page.goto(`/events/${SLUG}/seats`);
    await expect(seat(page, 1, 1)).toBeVisible();

    const results = await new AxeBuilder({ page }).analyze();
    const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
    expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target) })), null, 2)).toEqual([]);
  });
}
