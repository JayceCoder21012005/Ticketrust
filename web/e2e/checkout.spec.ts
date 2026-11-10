import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import { mockHub } from "./seat-hub";

/**
 * Seats -> buyer step -> order -> mock gateway -> result, against a scripted api (spec 08). The .NET integration tests
 * cover the real order, pay and webhook endpoints; this checks that the pages speak to them the way they expect.
 */
const SLUG = "son-tung-mtp-live";
const MIN = 60_000;

const user = {
  accessToken: "e2e-buyer-token",
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

type Status = "Pending" | "AwaitingPayment" | "PaymentFailed" | "Paid" | "Cancelled" | "Expired" | "RefundPending" | "Refunded";
type ApiOrder = ReturnType<typeof orderOf>;

function orderOf(eventId: string, status: Status, { expiresInMs = 8 * MIN, refundReason = null as string | null } = {}) {
  return {
    id: "o1",
    eventId,
    status,
    seats: [
      { index: 2, section: "A", row: "1", number: "3", tierId: "t1", price: 1_500_000 },
      { index: 3, section: "A", row: "1", number: "4", tierId: "t1", price: 1_500_000 },
    ],
    ga: [],
    total: 3_000_000,
    buyer: { name: "Nguyễn An", email: "an@example.com", phone: null },
    expiresAt: new Date(Date.now() + expiresInMs).toISOString(),
    serverNow: new Date().toISOString(),
    createdAt: new Date().toISOString(),
    paidAt: status === "Paid" ? new Date().toISOString() : null,
    refundReason,
    ticketCount: status === "Paid" ? 2 : null,
  };
}

type Backend = {
  order: ApiOrder;
  creates: { body: { holdId: string; buyer: { name: string; email: string; phone: string | null } }; key: string | undefined }[];
  pays: { key: string | undefined }[];
  completes: string[];
  /** Statuses `pay` answers with before it starts succeeding (a 503 is "gateway unavailable"). */
  payFailures: number[];
  /** A webhook arrives a poll later: the order keeps its status for `after` reads, then becomes `status`. */
  settle: { status: Status; after: number } | null;
};

type Event = { id: string; tiers: { id: string; kind: string }[] };

async function eventInfo(page: Page, baseURL: string): Promise<Event> {
  return (await (await page.request.get(`${baseURL}/api/v1/events/${SLUG}`)).json()) as Event;
}

/** Scripts the order, pay and mock-gateway endpoints on the browser side of the `/api` rewrite. */
async function arrange(context: BrowserContext, page: Page, baseURL: string, initial: Status = "Pending", orderOptions: Parameters<typeof orderOf>[2] = {}) {
  const event = await eventInfo(page, baseURL);
  const tierId = event.tiers.find((t) => t.kind === "Seated")!.id;
  const backend: Backend = { order: orderOf(event.id, initial, orderOptions), creates: [], pays: [], completes: [], payFailures: [], settle: null };
  const send = (route: Parameters<Parameters<BrowserContext["route"]>[1]>[0], status: number, json: unknown) => route.fulfill({ status, json });

  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await context.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: user }));

  await context.route(/\/api\/v1\/events\/[^/]+\/holds\/me$/, (route) =>
    send(route, 200, {
      holdId: "h1",
      seats: [2, 3].map((i) => ({ index: i, section: "A", row: "1", number: String(i + 1), tierId, price: 1_500_000 })),
      ga: [],
      total: 3_000_000,
      expiresAt: new Date(Date.now() + 8 * MIN).toISOString(),
      serverNow: new Date().toISOString(),
    }),
  );

  await context.route(/\/api\/v1\/orders$/, (route) => {
    backend.creates.push({ body: route.request().postDataJSON(), key: route.request().headers()["idempotency-key"] });
    backend.order = orderOf(event.id, "Pending");
    return send(route, 201, backend.order);
  });
  await context.route(/\/api\/v1\/orders\/o1$/, (route) => {
    if (backend.settle && backend.settle.after-- <= 0) {
      backend.order = { ...orderOf(event.id, backend.settle.status), refundReason: null };
      backend.settle = null;
    }

    return send(route, 200, { ...backend.order, serverNow: new Date().toISOString() });
  });
  await context.route(/\/api\/v1\/orders\/o1\/pay$/, (route) => {
    backend.pays.push({ key: route.request().headers()["idempotency-key"] });
    const refusal = backend.payFailures.shift();
    if (refusal) return send(route, refusal, { code: refusal === 503 ? "payment.gateway_unavailable" : "payment.rejected" });
    backend.order = { ...backend.order, status: "AwaitingPayment" };
    const session = `s${backend.pays.length}`;
    return send(route, 200, { redirectUrl: `${baseURL}/mock-gateway/${session}`, attemptId: "a1", expiresAt: backend.order.expiresAt, serverNow: new Date().toISOString() });
  });
  await context.route(/\/api\/v1\/orders\/o1\/cancel$/, (route) => {
    backend.order = { ...backend.order, status: "Cancelled" };
    return send(route, 200, backend.order);
  });

  const session = (id: string, status = "Pending") => ({
    sessionId: id,
    orderId: "o1",
    eventId: event.id,
    label: "Đơn hàng 0F3A9C1E",
    amount: 3_000_000,
    currency: "VND",
    status,
    expiresAt: new Date(Date.now() + 8 * MIN).toISOString(),
    returnUrl: `${baseURL}/checkout/o1/result`,
    serverNow: new Date().toISOString(),
  });
  await context.route(/\/api\/v1\/mock-gateway\/sessions\/[^/]+$/, (route) => send(route, 200, session(route.request().url().split("/").pop()!)));
  await context.route(/\/api\/v1\/mock-gateway\/sessions\/[^/]+\/complete$/, (route) => {
    const { outcome } = route.request().postDataJSON() as { outcome: string };
    backend.completes.push(outcome);
    if (outcome === "failed") backend.order = { ...backend.order, status: "PaymentFailed" };
    else backend.settle = { status: "Paid", after: 1 };
    return send(route, 200, session("s", outcome === "failed" ? "Failed" : "Succeeded"));
  });

  return { event, tierId, backend };
}

async function arrangeSeats(context: BrowserContext, event: Event, tierId: string) {
  const seats = Array.from({ length: 12 }, (_, i) => ({
    index: i,
    section: "A",
    row: String(Math.floor(i / 6) + 1),
    number: String((i % 6) + 1),
    x: 380 + (i % 6) * 24,
    y: 200 + Math.floor(i / 6) * 28,
    tierId,
  }));
  await context.addInitScript(
    (admission) => localStorage.setItem("tr.admission", JSON.stringify(admission)),
    { eventId: event.id, slug: SLUG, token: "adm-token", expiresAt: new Date(Date.now() + 10 * MIN).toISOString() },
  );
  await context.route(`**/api/v1/events/${event.id}/seat-map`, (route) =>
    route.fulfill({ json: { eventId: event.id, version: "e2e", sections: [{ name: "A", seatCount: 12 }], seats } }),
  );
  await context.route(`**/api/v1/events/${event.id}/availability`, (route) =>
    route.fulfill({ json: { serverNow: new Date().toISOString(), version: 1, unavailable: [], tiers: [] } }),
  );
  await mockHub(context);
}

const payButton = (page: Page) => page.getByRole("button", { name: "Thanh toán", exact: true });
const acceptTerms = (page: Page) => page.getByRole("checkbox", { name: /điều khoản/ }).check();

test("guarded: a signed-out visitor is sent to login from the order page", async ({ page }) => {
  await page.goto("/checkout/o1");

  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fcheckout%2Fo1/);
});

test("guarded: the buyer step needs a session too", async ({ page }) => {
  await page.goto(`/events/${SLUG}/checkout`);

  await expect(page).toHaveURL(new RegExp(`/login\\?returnUrl=%2Fevents%2F${SLUG}%2Fcheckout`));
});

test("happy path: seats, buyer step, order, mock gateway success, paid", async ({ context, page, baseURL }) => {
  const { event, tierId, backend } = await arrange(context, page, baseURL!);
  await arrangeSeats(context, event, tierId);
  await page.goto(`/events/${SLUG}/seats`);

  await page.getByRole("link", { name: "Tiếp tục thanh toán" }).first().click();
  await expect(page).toHaveURL(new RegExp(`/events/${SLUG}/checkout$`));
  await expect(page.getByRole("textbox", { name: "Họ và tên" })).toHaveValue("Nguyễn An");
  await page.getByRole("button", { name: "Tạo đơn hàng" }).click();

  await expect(page).toHaveURL(/\/checkout\/o1$/);
  expect(backend.creates).toHaveLength(1);
  expect(backend.creates[0].body).toMatchObject({ holdId: "h1", buyer: { name: "Nguyễn An", email: "an@example.com", phone: null } });
  expect(backend.creates[0].key).toMatch(/^[0-9a-f-]{36}$/);
  const summary = page.getByRole("region", { name: "Tóm tắt đơn hàng" });
  await expect(summary.getByRole("heading", { name: "Khu A" })).toBeVisible();
  await expect(summary.getByText(/3\.000\.000/)).toBeVisible();
  await expect(page.getByRole("timer").filter({ hasText: "Còn lại để thanh toán" })).toContainText(/0[78]:\d\d/);

  await acceptTerms(page);
  await payButton(page).click();
  await expect(page).toHaveURL(/\/mock-gateway\/s1$/);
  expect(backend.pays).toHaveLength(1);
  await expect(page.getByRole("heading", { level: 1, name: "Cổng thanh toán giả lập" })).toBeVisible();
  await page.getByRole("button", { name: "Thành công", exact: true }).click();

  await expect(page).toHaveURL(/\/checkout\/o1\/result$/);
  await expect(page.getByRole("heading", { name: "Đang xác nhận thanh toán" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Thanh toán thành công" })).toBeVisible({ timeout: 10_000 });
  await expect(page.getByRole("heading", { name: "Thanh toán thành công" })).toBeFocused();
  await expect(page.getByText("Bạn có 2 vé.")).toBeVisible();
  expect(backend.completes).toEqual(["succeeded"]);
});

test("a failed payment returns to the order, and paying again uses a fresh Idempotency-Key", async ({ context, page, baseURL }) => {
  const { backend } = await arrange(context, page, baseURL!);
  await page.goto("/checkout/o1");
  await acceptTerms(page);
  await payButton(page).click();
  await expect(page).toHaveURL(/\/mock-gateway\/s1$/);

  await page.getByRole("button", { name: "Thất bại", exact: true }).click();

  await expect(page).toHaveURL(/\/checkout\/o1\/result$/);
  await expect(page.getByRole("heading", { name: "Thanh toán không thành công" })).toBeVisible();
  await page.getByRole("link", { name: "Thử lại" }).click();
  await expect(page).toHaveURL(/\/checkout\/o1$/);
  await expect(page.getByText(/Lần thanh toán trước không thành công/)).toBeVisible();
  await acceptTerms(page);
  await payButton(page).click();
  await expect(page).toHaveURL(/\/mock-gateway\/s2$/);

  expect(backend.pays).toHaveLength(2);
  expect(backend.pays[0].key).toMatch(/^[0-9a-f-]{36}$/);
  expect(backend.pays[1].key).toMatch(/^[0-9a-f-]{36}$/);
  expect(backend.pays[1].key).not.toBe(backend.pays[0].key);
});

test("gateway unavailable: says so, keeps the order, and the retry carries the same Idempotency-Key", async ({ context, page, baseURL }) => {
  const { backend } = await arrange(context, page, baseURL!);
  backend.payFailures.push(503);
  await page.goto("/checkout/o1");
  await acceptTerms(page);

  await payButton(page).click();

  await expect(page.getByRole("alert").filter({ hasText: "Cổng thanh toán tạm thời gián đoạn, thử lại sau" })).toBeVisible();
  await expect(payButton(page)).toBeEnabled();
  await expect(payButton(page)).toBeFocused();
  await payButton(page).click();
  await expect(page).toHaveURL(/\/mock-gateway\/s2$/);
  expect(backend.pays).toHaveLength(2);
  expect(backend.pays[1].key).toBe(backend.pays[0].key);
});

test("an order whose time runs out stops accepting payment", async ({ context, page, baseURL }) => {
  await arrange(context, page, baseURL!, "Pending", { expiresInMs: 3_000 });
  await page.goto("/checkout/o1");
  await expect(payButton(page)).toBeVisible();

  const heading = page.getByRole("heading", { name: "Hết thời gian thanh toán" });

  await expect(heading).toBeVisible({ timeout: 8_000 });
  await expect(heading).toBeFocused();
  await expect(payButton(page)).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Xem sự kiện" })).toHaveAttribute("href", "/events");
});

test("a Pending order can be cancelled; one with a payment under way cannot", async ({ context, page, baseURL }) => {
  await arrange(context, page, baseURL!);
  await page.goto("/checkout/o1");

  await page.getByRole("button", { name: "Huỷ đơn" }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Huỷ đơn" }).click();

  await expect(page).toHaveURL(/\/checkout\/o1\/result$/);
  await expect(page.getByRole("heading", { name: "Đơn hàng đã huỷ" })).toBeVisible();

  const second = await page.context().newPage();
  await arrange(context, second, baseURL!, "AwaitingPayment");
  await second.goto("/checkout/o1");
  await expect(payButton(second)).toBeVisible();
  await expect(second.getByRole("button", { name: "Huỷ đơn" })).toHaveCount(0);
});

test("a late payment is explained as a refund with its reason", async ({ context, page, baseURL }) => {
  await arrange(context, page, baseURL!, "RefundPending", { refundReason: "seats_unavailable" });
  await page.goto("/checkout/o1/result");

  await expect(page.getByRole("heading", { name: "Đang hoàn tiền" })).toBeVisible();
  await expect(page.getByText(/ghế đã có người khác mua/)).toBeVisible();
});

test("the mock gateway page says it is unavailable when the api has no mock gateway", async ({ context, page, baseURL }) => {
  await arrange(context, page, baseURL!);
  await context.route(/\/api\/v1\/mock-gateway\/sessions\/[^/]+$/, (route) => route.fulfill({ status: 404, json: { code: "mock_gateway.session_not_found" } }));
  await page.goto("/mock-gateway/s1");

  await expect(page.getByRole("heading", { name: "Cổng thanh toán giả lập không khả dụng" })).toBeVisible();
  await expect(page.locator("main").getByRole("button")).toHaveCount(0);
});

test.describe("confetti", () => {
  test("celebrates a paid order", async ({ context, page, baseURL }) => {
    await arrange(context, page, baseURL!, "Paid");
    await page.goto("/checkout/o1/result");

    await expect(page.getByRole("heading", { name: "Thanh toán thành công" })).toBeVisible();
    await expect(page.getByTestId("confetti")).toBeAttached();
  });

  test.describe("reduced motion", () => {
    test.use({ contextOptions: { reducedMotion: "reduce" } });

    test("shows none", async ({ context, page, baseURL }) => {
      await arrange(context, page, baseURL!, "Paid");
      await page.goto("/checkout/o1/result");

      await expect(page.getByRole("heading", { name: "Thanh toán thành công" })).toBeVisible();
      await expect(page.getByTestId("confetti")).toHaveCount(0);
    });
  });
});

type Scenario = { name: string; open: (page: Page) => Promise<void>; status?: Status; refundReason?: string };

const scenarios: Scenario[] = [
  { name: "buyer step", open: async (page) => { await page.goto(`/events/${SLUG}/checkout`); await expect(page.getByRole("button", { name: "Tạo đơn hàng" })).toBeVisible(); } },
  { name: "order", open: async (page) => { await page.goto("/checkout/o1"); await expect(payButton(page)).toBeVisible(); } },
  { name: "order with a failed payment", status: "PaymentFailed", open: async (page) => { await page.goto("/checkout/o1"); await expect(page.getByText(/Lần thanh toán trước/)).toBeVisible(); } },
  { name: "result: confirming", status: "AwaitingPayment", open: async (page) => { await page.goto("/checkout/o1/result"); await expect(page.getByRole("heading", { name: "Đang xác nhận thanh toán" })).toBeVisible(); } },
  { name: "result: paid", status: "Paid", open: async (page) => { await page.goto("/checkout/o1/result"); await expect(page.getByRole("heading", { name: "Thanh toán thành công" })).toBeVisible(); } },
  { name: "result: failed", status: "PaymentFailed", open: async (page) => { await page.goto("/checkout/o1/result"); await expect(page.getByRole("heading", { name: "Thanh toán không thành công" })).toBeVisible(); } },
  { name: "result: refunding", status: "RefundPending", refundReason: "event_cancelled", open: async (page) => { await page.goto("/checkout/o1/result"); await expect(page.getByRole("heading", { name: "Đang hoàn tiền" })).toBeVisible(); } },
  { name: "result: expired", status: "Expired", open: async (page) => { await page.goto("/checkout/o1/result"); await expect(page.getByRole("heading", { name: "Đơn hàng đã hết hạn" })).toBeVisible(); } },
  { name: "mock gateway", open: async (page) => { await page.goto("/mock-gateway/s1"); await expect(page.getByRole("button", { name: "Gửi webhook 2 lần" })).toBeVisible(); } },
];

for (const colorScheme of ["light", "dark"] as const) {
  for (const scenario of scenarios) {
    test(`${scenario.name} has no serious accessibility violations (${colorScheme})`, async ({ context, page, baseURL }) => {
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      await arrange(context, page, baseURL!, scenario.status ?? "Pending", { refundReason: scenario.refundReason ?? null });
      await scenario.open(page);

      const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
      const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
      expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target) })), null, 2)).toEqual([]);
    });
  }
}
