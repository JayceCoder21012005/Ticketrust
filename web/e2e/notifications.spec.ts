import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import { mockSignalRHub } from "./seat-hub";

/**
 * The notification bell, the popover, the list page and the live toast (spec 09) against a scripted api and a scripted
 * `/hubs/user`: the .NET integration tests cover the real ones. Same `context.route` pattern as orders.spec.ts.
 */
const MIN = 60_000;

const user = {
  accessToken: "e2e-buyer-token",
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

const orderId = (n: number) => `0000000${n}-aaaa-bbbb-cccc-dddddddddddd`;

type Item = { id: string; type: string; title: string; body: string; link: string | null; read: boolean; createdAt: string };

const paidNotification = (n: number, extra: Partial<Item> = {}): Item => ({
  id: `1000000${n}-aaaa-bbbb-cccc-dddddddddddd`,
  type: "OrderPaid",
  title: `Thanh toán thành công ${n}`,
  body: `Đơn #0000000${n} đã được thanh toán. Vé đang được chuẩn bị.`,
  link: `/account/orders/${orderId(n)}`,
  read: false,
  createdAt: "2026-12-01T03:02:00Z",
  ...extra,
});

function orderDetail(n: number) {
  return {
    id: orderId(n),
    eventId: "e1",
    status: "Paid",
    seats: [{ index: 2, section: "A", row: "1", number: "3", tierId: "t1", price: 750_000 }],
    ga: [],
    total: 750_000,
    buyer: { name: "Nguyễn An", email: "an@example.com", phone: "0901234567" },
    expiresAt: new Date(Date.now() + 8 * MIN).toISOString(),
    serverNow: new Date().toISOString(),
    createdAt: "2026-12-01T03:00:00Z",
    paidAt: "2026-12-01T03:02:00Z",
    refundReason: null,
    ticketCount: 1,
  };
}

type Inbox = { pages: Record<string, { items: Item[]; nextCursor: string | null }>; reads: string[]; readAll: number; listCalls: (string | null)[] };

/** Signs the visitor in, scripts the inbox (pages keyed by cursor) and the order the notification points at, and the user hub. */
async function arrange(context: BrowserContext, baseURL: string, pages: Inbox["pages"]) {
  const inbox: Inbox = { pages, reads: [], readAll: 0, listCalls: [] };
  const unread = () => Object.values(inbox.pages).flatMap((page) => page.items).filter((item) => !item.read).length;
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await context.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: user }));
  await context.route(/\/api\/v1\/me\/notifications(\?.*)?$/, (route) => {
    const cursor = new URL(route.request().url()).searchParams.get("cursor");
    inbox.listCalls.push(cursor);
    return route.fulfill({ json: { ...(inbox.pages[cursor ?? "first"] ?? { items: [], nextCursor: null }), unreadCount: unread() } });
  });
  await context.route("**/api/v1/me/notifications/read-all", (route) => {
    inbox.readAll++;
    Object.values(inbox.pages).forEach((page) => page.items.forEach((item) => (item.read = true)));
    return route.fulfill({ status: 204 });
  });
  await context.route(/\/api\/v1\/me\/notifications\/[^/]+\/read$/, (route) => {
    const id = route.request().url().split("/").at(-2)!;
    inbox.reads.push(id);
    Object.values(inbox.pages).forEach((page) => page.items.forEach((item) => item.id === id && (item.read = true)));
    return route.fulfill({ status: 204 });
  });
  await context.route(/\/api\/v1\/me\/orders(\?.*)?$/, (route) => route.fulfill({ json: { items: [], nextCursor: null } }));
  await context.route(/\/api\/v1\/orders\/[^/]+$/, (route) => route.fulfill({ json: { ...orderDetail(1), serverNow: new Date().toISOString() } }));
  const hub = await mockSignalRHub(context, "user");
  return { inbox, hub };
}

const bell = (page: Page, name: string | RegExp = /thông báo/i) => page.getByRole("button", { name });

test.describe("bell", () => {
  test("shows 1 unread, lists it in the popover, and the item opens its order and clears the badge", async ({ context, page, baseURL }) => {
    const { inbox } = await arrange(context, baseURL!, { first: { items: [paidNotification(1)], nextCursor: null } });
    await page.goto("/account/orders");

    await bell(page, "1 thông báo chưa đọc").click();
    const popover = page.getByRole("dialog", { name: "Thông báo" });
    await expect(popover.getByRole("listitem")).toHaveCount(1);
    await expect(popover).toContainText("Thanh toán thành công 1");
    await popover.getByRole("link", { name: /Thanh toán thành công 1/ }).click();

    await expect(page).toHaveURL(new RegExp(`/account/orders/${orderId(1)}$`));
    await expect(popover).toBeHidden();
    await expect(bell(page, "Thông báo")).toBeVisible();
    expect(inbox.reads).toEqual([paidNotification(1).id]);
  });

  test("is reachable by keyboard: Enter opens the popover, Escape closes it and returns focus", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!, { first: { items: [paidNotification(1)], nextCursor: null } });
    await page.goto("/account/orders");

    const button = bell(page, "1 thông báo chưa đọc");
    await button.focus();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("dialog", { name: "Thông báo" })).toBeVisible();
    await page.keyboard.press("Escape");

    await expect(page.getByRole("dialog")).toBeHidden();
    await expect(button).toBeFocused();
  });

  test("mark all read clears the badge", async ({ context, page, baseURL }) => {
    const { inbox } = await arrange(context, baseURL!, { first: { items: [paidNotification(1), paidNotification(2)], nextCursor: null } });
    await page.goto("/account/orders");

    await bell(page, "2 thông báo chưa đọc").click();
    await page.getByRole("button", { name: "Đánh dấu tất cả đã đọc" }).click();

    await expect(bell(page, "Thông báo")).toBeVisible();
    expect(inbox.readAll).toBe(1);
  });

  test("has no bell for a visitor without a session hint", async ({ page }) => {
    await page.goto("/events");

    await expect(page.getByRole("link", { name: "Đăng nhập" })).toBeVisible();
    await expect(bell(page)).toHaveCount(0);
  });

  test("works on a public page too, without opening a hub connection", async ({ context, page, baseURL }) => {
    const { hub } = await arrange(context, baseURL!, { first: { items: [paidNotification(1)], nextCursor: null } });
    await page.goto("/events");

    await expect(bell(page, "1 thông báo chưa đọc")).toBeVisible();
    expect(hub.state.connections).toBe(0);
  });
});

test.describe("live", () => {
  test("a pushed notification shows a toast, raises the badge, and the toast opens the order", async ({ context, page, baseURL }) => {
    const { hub } = await arrange(context, baseURL!, { first: { items: [paidNotification(1, { read: true })], nextCursor: null } });
    await page.goto("/account/orders");
    await expect(bell(page, "Thông báo")).toBeVisible();
    // A message must not overtake the handshake reply.
    await expect.poll(() => hub.state.handshakes).toBe(1);

    hub.send("Notification", paidNotification(2));

    const toast = page.locator("[data-sonner-toast]").filter({ hasText: "Thanh toán thành công 2" });
    await expect(toast).toBeVisible();
    await expect(bell(page, "1 thông báo chưa đọc")).toBeVisible();
    await toast.getByRole("button", { name: "Xem" }).click();
    await expect(page).toHaveURL(new RegExp(`/account/orders/${orderId(2)}$`));
  });

  test("keeps its one connection while the buyer moves between pages", async ({ context, page, baseURL }) => {
    const { hub } = await arrange(context, baseURL!, { first: { items: [paidNotification(1)], nextCursor: null } });
    await page.goto("/account/orders");
    await expect.poll(() => hub.state.connections).toBe(1);

    await bell(page, "1 thông báo chưa đọc").click();
    await page.getByRole("link", { name: "Xem tất cả" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Thông báo" })).toBeVisible();
    await page.waitForTimeout(500);

    expect(hub.state.connections).toBe(1);
  });
});

test.describe("list page", () => {
  const twoPages = () => ({
    first: { items: [paidNotification(3), paidNotification(2)], nextCursor: "page-2" },
    "page-2": { items: [paidNotification(1, { read: true })], nextCursor: null },
  });

  test("loads the next page with the api's cursor", async ({ context, page, baseURL }) => {
    const { inbox } = await arrange(context, baseURL!, twoPages());
    await page.goto("/account/notifications");

    await expect(page.getByRole("heading", { level: 1, name: "Thông báo" })).toBeFocused();
    const rows = page.getByRole("list", { name: "Danh sách thông báo" }).getByRole("listitem");
    await expect(rows).toHaveCount(2);
    await page.getByRole("button", { name: "Tải thêm" }).click();

    await expect(rows).toHaveCount(3);
    expect(inbox.listCalls).toEqual([null, "page-2"]);
    await expect(page.getByRole("button", { name: "Tải thêm" })).toHaveCount(0);
  });

  test("shows the empty state", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!, { first: { items: [], nextCursor: null } });
    await page.goto("/account/notifications");

    await expect(page.getByText("Bạn chưa có thông báo nào")).toBeVisible();
  });
});

for (const colorScheme of ["light", "dark"] as const) {
  const scenarios: { name: string; open: (page: Page) => Promise<void> }[] = [
    {
      name: "the popover",
      open: async (page) => {
        await page.goto("/account/orders");
        await bell(page, "2 thông báo chưa đọc").click();
        await expect(page.getByRole("dialog", { name: "Thông báo" }).getByRole("listitem")).toHaveCount(3);
      },
    },
    {
      name: "the list page",
      open: async (page) => {
        await page.goto("/account/notifications");
        await expect(page.getByRole("list", { name: "Danh sách thông báo" }).getByRole("listitem")).toHaveCount(3);
      },
    },
  ];

  for (const scenario of scenarios) {
    test(`${scenario.name} has no serious accessibility violations (${colorScheme})`, async ({ context, page, baseURL }) => {
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      await arrange(context, baseURL!, { first: { items: [paidNotification(3), paidNotification(2), paidNotification(1, { read: true })], nextCursor: null } });
      await scenario.open(page);

      const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
      const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
      expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target) })), null, 2)).toEqual([]);
    });
  }
}
