import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";

/**
 * `/account/orders` and `/account/orders/[orderId]` against a scripted api (spec 08): the same `context.route` pattern
 * as checkout.spec.ts. The real list and order endpoints are covered by the .NET integration tests.
 */
const MIN = 60_000;

const user = {
  accessToken: "e2e-buyer-token",
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

type Status = "Pending" | "AwaitingPayment" | "PaymentFailed" | "Paid" | "Cancelled" | "Expired" | "RefundPending" | "Refunded";

const id = (n: number) => `0000000${n}-aaaa-bbbb-cccc-dddddddddddd`;

const summary = (n: number, status: Status) => ({
  id: id(n),
  eventId: "e1",
  eventTitle: `Đêm nhạc số ${n}`,
  eventSlug: `dem-nhac-${n}`,
  eventStartsAt: "2026-12-20T13:00:00Z",
  status,
  total: n * 1_500_000,
  itemCount: 2,
  createdAt: `2026-11-0${n}T03:00:00Z`,
  paidAt: status === "Paid" ? `2026-11-0${n}T03:02:00Z` : null,
});

function detailOf(n: number, status: Status, refundReason: string | null = null) {
  const paid = status === "Paid" || status === "RefundPending" || status === "Refunded";
  return {
    id: id(n),
    eventId: "e1",
    status,
    seats: [
      { index: 2, section: "A", row: "1", number: "3", tierId: "t1", price: 750_000 },
      { index: 3, section: "A", row: "1", number: "4", tierId: "t1", price: 750_000 },
    ],
    ga: [],
    total: n * 1_500_000,
    buyer: { name: "Nguyễn An", email: "an@example.com", phone: "0901234567" },
    expiresAt: new Date(Date.now() + 8 * MIN).toISOString(),
    serverNow: new Date().toISOString(),
    createdAt: `2026-11-0${n}T03:00:00Z`,
    paidAt: paid ? `2026-11-0${n}T03:02:00Z` : null,
    refundReason,
    ticketCount: status === "Paid" ? 2 : null,
  };
}

type Backend = { listCalls: (string | null)[]; orders: Map<string, ReturnType<typeof detailOf>> };

/** Signs the visitor in and scripts `GET /me/orders` (pages keyed by cursor) and `GET /orders/{id}`. */
async function arrange(context: BrowserContext, baseURL: string, pages: Record<string, { items: ReturnType<typeof summary>[]; nextCursor: string | null }>, orders: ReturnType<typeof detailOf>[] = []) {
  const backend: Backend = { listCalls: [], orders: new Map(orders.map((o) => [o.id, o])) };
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await context.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: user }));
  await context.route(/\/api\/v1\/me\/orders(\?.*)?$/, (route) => {
    const cursor = new URL(route.request().url()).searchParams.get("cursor");
    backend.listCalls.push(cursor);
    return route.fulfill({ json: pages[cursor ?? "first"] ?? { items: [], nextCursor: null } });
  });
  await context.route(/\/api\/v1\/orders\/[^/]+$/, (route) => {
    const order = backend.orders.get(route.request().url().split("/").pop()!);
    return order ? route.fulfill({ json: { ...order, serverNow: new Date().toISOString() } }) : route.fulfill({ status: 404, json: { code: "order.not_found" } });
  });
  return backend;
}

const twoPages = {
  first: { items: [summary(3, "Paid"), summary(2, "Pending")], nextCursor: "page-2" },
  "page-2": { items: [summary(1, "Cancelled")], nextCursor: null },
};

test.describe("order list", () => {
  test("shows the first page newest first and loads the next with the api's cursor", async ({ context, page, baseURL }) => {
    const backend = await arrange(context, baseURL!, twoPages);
    await page.goto("/account/orders");

    await expect(page.getByRole("heading", { level: 1, name: "Đơn hàng của tôi" })).toBeFocused();
    const rows = page.getByRole("listitem");
    await expect(rows).toHaveCount(2);
    await expect(rows.nth(0)).toContainText("Đêm nhạc số 3");
    await expect(rows.nth(0)).toContainText("#00000003");
    await expect(rows.nth(0)).toContainText("4.500.000");
    await expect(rows.nth(0)).toContainText("Đã thanh toán");
    await expect(rows.nth(1).getByRole("link", { name: /Tiếp tục thanh toán/ })).toHaveAttribute("href", `/checkout/${id(2)}`);

    await page.getByRole("button", { name: "Xem thêm" }).click();

    await expect(rows).toHaveCount(3);
    await expect(rows.nth(2)).toContainText("Đã huỷ");
    await expect(rows.nth(2).getByRole("link", { name: /Đêm nhạc số 1/ })).toBeFocused();
    await expect(page.getByRole("button", { name: "Xem thêm" })).toHaveCount(0);
    expect(backend.listCalls).toEqual([null, "page-2"]);
  });

  test("shows an empty state that points to the events", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!, { first: { items: [], nextCursor: null } });
    await page.goto("/account/orders");

    await expect(page.getByText("Bạn chưa có đơn hàng nào")).toBeVisible();
    await expect(page.getByRole("link", { name: "Khám phá sự kiện" })).toHaveAttribute("href", "/events");
  });

  test("a signed-out visitor is sent to login and comes back afterwards", async ({ page }) => {
    await page.goto("/account/orders");
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Faccount%2Forders/);
  });

  test("the account page links to it", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!, twoPages);
    await context.route("**/api/v1/me", (route) => route.fulfill({ json: user.user }));
    await page.goto("/account");

    await page.getByRole("link", { name: "Đơn hàng của tôi" }).click();
    await expect(page).toHaveURL(/\/account\/orders$/);
    await expect(page.getByRole("heading", { level: 1, name: "Đơn hàng của tôi" })).toBeVisible();
  });

  test("a row opens the detail, which knows the event from the list", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!, twoPages, [detailOf(3, "Paid")]);
    await page.goto("/account/orders");

    await page.getByRole("link", { name: /Đêm nhạc số 3/ }).click();

    await expect(page).toHaveURL(new RegExp(`/account/orders/${id(3)}$`));
    await expect(page.getByRole("heading", { level: 1, name: "Đơn hàng #00000003" })).toBeFocused();
    // The list stays mounted (hidden) behind the detail, so look at the detail header only.
    await expect(page.locator("header span", { hasText: "Đêm nhạc số 3" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Xem sự kiện" })).toHaveAttribute("href", "/events/dem-nhac-3");
  });
});

test.describe("order detail", () => {
  test("a paid order walks through booked, awaiting and paid with its times", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!, twoPages, [detailOf(3, "Paid")]);
    await page.goto(`/account/orders/${id(3)}`);

    const steps = page.getByRole("list", { name: "Các bước của đơn hàng" }).getByRole("listitem");
    await expect(steps).toHaveCount(3);
    await expect(steps.nth(0)).toContainText("Đặt chỗ");
    await expect(steps.nth(1)).toContainText("Chờ thanh toán");
    await expect(steps.nth(2)).toContainText("Đã thanh toán");
    await expect(steps.nth(2)).toHaveAttribute("aria-current", "step");
    await expect(steps.nth(2)).toContainText("10:02 03/11/2026");
    await expect(page.getByText("Bạn có 2 vé.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Xem vé" })).toBeDisabled();
    await expect(page.getByText("Nguyễn An")).toBeVisible();
    await expect(page.getByText("4.500.000")).toBeVisible();
  });

  test("a refund in progress names its reason and promises no number of days", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!, twoPages, [detailOf(3, "RefundPending", "event_cancelled")]);
    await page.goto(`/account/orders/${id(3)}`);

    const steps = page.getByRole("list", { name: "Các bước của đơn hàng" }).getByRole("listitem");
    await expect(steps).toHaveCount(5);
    await expect(steps.nth(3)).toHaveAttribute("aria-current", "step");
    await expect(steps.nth(3)).toContainText("Đang hoàn tiền");
    await expect(steps.nth(3)).toContainText("Nhà tổ chức đã huỷ sự kiện này.");
    await expect(steps.nth(3)).toContainText("Thời gian tiền về tuỳ thuộc vào ngân hàng của bạn");
    await expect(steps.nth(4)).toContainText("Đã hoàn tiền");
    await expect(page.getByRole("link", { name: "Tiếp tục thanh toán" })).toHaveCount(0);
  });

  test("an open order can go back to paying", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!, twoPages, [detailOf(2, "AwaitingPayment")]);
    await page.goto(`/account/orders/${id(2)}`);

    await expect(page.getByRole("link", { name: "Tiếp tục thanh toán" })).toHaveAttribute("href", `/checkout/${id(2)}`);
  });

  test("an order that is not the visitor's is not found", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!, twoPages);
    await page.goto(`/account/orders/${id(9)}`);

    await expect(page.getByRole("heading", { name: "Không tìm thấy đơn hàng" })).toBeFocused();
  });
});

type Scenario = { name: string; open: (page: Page) => Promise<void>; pages?: typeof twoPages | { first: { items: []; nextCursor: null } }; orders?: ReturnType<typeof detailOf>[] };

const scenarios: Scenario[] = [
  { name: "list", open: async (page) => { await page.goto("/account/orders"); await expect(page.getByRole("listitem")).toHaveCount(2); } },
  { name: "list with every status", pages: { first: { items: (["Pending", "AwaitingPayment", "PaymentFailed", "Paid", "Cancelled", "Expired", "RefundPending", "Refunded"] as const).map((s, i) => summary((i % 3) + 1, s)).map((o, i) => ({ ...o, id: id(i + 1) })), nextCursor: "page-2" }, "page-2": twoPages["page-2"] }, open: async (page) => { await page.goto("/account/orders"); await expect(page.getByRole("listitem")).toHaveCount(8); } },
  { name: "empty list", pages: { first: { items: [], nextCursor: null } }, open: async (page) => { await page.goto("/account/orders"); await expect(page.getByText("Bạn chưa có đơn hàng nào")).toBeVisible(); } },
  { name: "paid order", orders: [detailOf(3, "Paid")], open: async (page) => { await page.goto(`/account/orders/${id(3)}`); await expect(page.getByRole("list", { name: "Các bước của đơn hàng" })).toBeVisible(); } },
  { name: "refunding order", orders: [detailOf(3, "RefundPending", "seats_unavailable")], open: async (page) => { await page.goto(`/account/orders/${id(3)}`); await expect(page.getByText("Đang hoàn tiền").first()).toBeVisible(); } },
  { name: "failed payment", orders: [detailOf(3, "PaymentFailed")], open: async (page) => { await page.goto(`/account/orders/${id(3)}`); await expect(page.getByText("Thanh toán thất bại").first()).toBeVisible(); } },
  { name: "expired order", orders: [detailOf(3, "Expired")], open: async (page) => { await page.goto(`/account/orders/${id(3)}`); await expect(page.getByText("Hết hạn").first()).toBeVisible(); } },
];

for (const colorScheme of ["light", "dark"] as const) {
  for (const scenario of scenarios) {
    test(`${scenario.name} has no serious accessibility violations (${colorScheme})`, async ({ context, page, baseURL }) => {
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      await arrange(context, baseURL!, scenario.pages ?? twoPages, scenario.orders);
      await scenario.open(page);

      const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
      const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
      expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target) })), null, 2)).toEqual([]);
    });
  }
}
