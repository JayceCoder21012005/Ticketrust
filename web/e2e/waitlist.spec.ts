import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";

/**
 * Spec 13 on the web against scripted waitlist endpoints (the .NET integration tests cover the real ones): a sold-out event
 * offers the waitlist, the dialog joins it, the offer page counts down and continues in checkout, and the account hub lists
 * the entries. Same `context.route` pattern as follow.spec.ts.
 */
const PATH = "/events/son-tung-mtp-live";
const MIN = 60_000;

const user = {
  accessToken: "e2e-buyer-token",
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

const entry = (over: object = {}) => ({
  entryId: "w1",
  eventId: "e1",
  status: "Waiting",
  position: 1,
  tierId: null,
  quantity: 2,
  joinedAt: new Date().toISOString(),
  offer: null,
  ...over,
});

async function signIn(context: BrowserContext, baseURL: string) {
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await context.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: user }));
  await context.route(/\/api\/v1\/me\/notifications(\?.*)?$/, (route) => route.fulfill({ json: { items: [], nextCursor: null, unreadCount: 0 } }));
  await context.route(/\/api\/v1\/events\/[^/]+\/follow$/, (route) => route.fulfill({ json: { following: false } }));
}

async function soldOut(context: BrowserContext) {
  await context.route(/\/api\/v1\/events\/[^/]+\/availability$/, (route) =>
    route.fulfill({ json: { serverNow: new Date().toISOString(), version: 0, unavailable: [], tiers: [{ tierId: "t-sold-out", remaining: 0 }] } }),
  );
}

async function expectNoSeriousA11yIssues(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target) })), null, 2)).toEqual([]);
}

test("a visitor of a sold-out event is sent to login, then back to the event", async ({ page, context }) => {
  await soldOut(context);
  await page.goto(PATH);

  await expect(page.locator("a:visible", { hasText: "Hết vé — Vào danh sách chờ" }).first()).toHaveAttribute("href", `/login?returnUrl=${encodeURIComponent(PATH)}`);
});

test("a signed-in fan joins the waitlist from the dialog and sees their place in line", async ({ page, context, baseURL }) => {
  await signIn(context, baseURL!);
  await soldOut(context);
  let joined: unknown = null;
  await context.route(/\/api\/v1\/events\/[^/]+\/waitlist\/me$/, (route) =>
    joined
      ? route.fulfill({ json: joined })
      : route.fulfill({ status: 404, contentType: "application/problem+json", body: JSON.stringify({ status: 404, code: "waitlist.not_joined" }) }),
  );
  let body: unknown = null;
  await context.route(/\/api\/v1\/events\/[^/]+\/waitlist$/, async (route) => {
    body = route.request().postDataJSON();
    joined = entry();
    await route.fulfill({ json: joined });
  });

  await page.goto(PATH);
  await page.locator("button:visible", { hasText: "Hết vé — Vào danh sách chờ" }).first().click();
  const dialog = page.getByRole("dialog", { name: "Vào danh sách chờ" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole("combobox", { name: "Hạng vé" })).toHaveValue("");
  await dialog.getByRole("button", { name: "Thêm một vé" }).click();
  await expectNoSeriousA11yIssues(page);
  await dialog.getByRole("button", { name: "Vào danh sách chờ" }).click();

  await expect(dialog).toBeHidden();
  await expect(page.locator("button:visible", { hasText: "Đang chờ · vị trí #1" }).first()).toBeVisible();
  expect(body).toEqual({ tierId: null, quantity: 2 });
});

test("the offer page counts down and continues in checkout", async ({ page, context, baseURL }) => {
  await signIn(context, baseURL!);
  const expiresAt = new Date(Date.now() + 15 * MIN).toISOString();
  await context.route(/\/api\/v1\/waitlist\/offers\/w1$/, (route) =>
    route.fulfill({
      json: {
        offerId: "w1",
        eventId: "e1",
        eventSlug: "son-tung-mtp-live",
        eventTitle: "Sơn Tùng M-TP Live",
        startsAt: "2026-12-20T13:00:00Z",
        expiresAt,
        hold: {
          holdId: "h1",
          seats: [{ index: 5, section: "A", row: "1", number: "6", tierId: "vip", price: 2_000_000 }],
          ga: [],
          total: 2_000_000,
          expiresAt,
          serverNow: new Date().toISOString(),
        },
      },
    }),
  );
  await context.route(/\/api\/v1\/waitlist\/offers\/w1\/accept$/, (route) =>
    route.fulfill({ json: { holdId: "h1", eventId: "e1", eventSlug: "son-tung-mtp-live", expiresAt } }),
  );

  await page.goto("/waitlist/offers/w1");
  await expect(page.getByRole("heading", { level: 1, name: "Có vé cho bạn!" })).toBeVisible();
  await expect(page.getByRole("timer")).toContainText(/1[45]:\d\d/);
  await expectNoSeriousA11yIssues(page);
  await page.getByRole("button", { name: "Mua ngay" }).click();

  await expect(page).toHaveURL(/\/events\/son-tung-mtp-live\/checkout$/);
});

test("the account hub lists the waitlists with the way to a live offer", async ({ page, context, baseURL }) => {
  await signIn(context, baseURL!);
  await context.route(/\/api\/v1\/me\/waitlist$/, (route) =>
    route.fulfill({
      json: [
        { entry: entry({ position: 4 }), eventSlug: "a", eventTitle: "Sự kiện A", startsAt: "2026-12-01T13:00:00Z", city: "Hà Nội", coverImageUrl: null, tierName: null },
        {
          entry: entry({ entryId: "w2", status: "Offered", position: null, offer: { offerId: "w2", expiresAt: new Date(Date.now() + 10 * MIN).toISOString() } }),
          eventSlug: "b",
          eventTitle: "Sự kiện B",
          startsAt: "2026-12-02T13:00:00Z",
          city: "Đà Nẵng",
          coverImageUrl: null,
          tierName: "VIP",
        },
      ],
    }),
  );

  await page.goto("/account/waitlist");
  await expect(page.getByRole("heading", { level: 1, name: "Danh sách chờ" })).toBeVisible();
  await expect(page.getByText("Đang chờ · vị trí #4")).toBeVisible();
  await expect(page.getByRole("link", { name: "Xem lời mời" })).toHaveAttribute("href", "/waitlist/offers/w2");
  await expect(page.getByRole("link", { name: "Danh sách chờ", exact: true })).toHaveAttribute("aria-current", "page");
  await expectNoSeriousA11yIssues(page);
});
