import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";

/**
 * Spec 14 on the web against scripted endpoints (the .NET integration tests cover the real ones): the wallet's "…" menu resells
 * (price capped at face value) or sends a ticket, a sold-out event lists resale tickets and "Mua" continues in checkout, and the
 * recipient accepts a transfer. Same `context.route` pattern as waitlist.spec.ts.
 */
const MIN = 60_000;
const TICKET = "00000001-aaaa-bbbb-cccc-eeeeeeeeeeee";
const SECRET = Buffer.from(Array.from({ length: 32 }, (_, i) => i * 7)).toString("base64");

const user = {
  accessToken: "e2e-buyer-token",
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

const detail = (over: Record<string, unknown> = {}) => ({
  id: TICKET,
  orderId: "o1",
  tierName: "Hạng VIP",
  seat: { section: "B", row: "4", number: "17" },
  status: "Valid",
  holderName: "Nguyễn An",
  orderTicketIds: [TICKET],
  event: {
    eventId: "e1",
    slug: "son-tung-mtp-live",
    title: "Sơn Tùng M-TP Live",
    coverImageUrl: null,
    startsAt: "2099-12-20T13:00:00Z",
    endsAt: "2099-12-20T16:00:00Z",
    venueName: "SVĐ Mỹ Đình",
    city: "Hà Nội",
    status: "Published",
  },
  resale: { faceValue: 2_000_000, currency: "VND", canChange: true, listing: null, transfer: null },
  ...over,
});

async function signIn(context: BrowserContext, baseURL: string) {
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await context.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: user }));
  await context.route(/\/api\/v1\/me\/notifications(\?.*)?$/, (route) => route.fulfill({ json: { items: [], nextCursor: null, unreadCount: 0 } }));
  await context.route(/\/api\/v1\/events\/[^/]+\/follow$/, (route) => route.fulfill({ json: { following: false } }));
}

async function expectNoSeriousA11yIssues(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target) })), null, 2)).toEqual([]);
}

async function wallet(context: BrowserContext, ticket: () => ReturnType<typeof detail>) {
  await context.route(/\/api\/v1\/tickets\/[^/]+$/, (route) => route.fulfill({ json: ticket() }));
  await context.route(/\/api\/v1\/tickets\/[^/]+\/qr-key$/, (route) =>
    route.fulfill({ json: { ticketId: TICKET, secret: SECRET, stepSeconds: 15, format: "TR1" } }),
  );
  await context.route(/\/api\/v1\/time$/, (route) => route.fulfill({ json: { serverNow: new Date().toISOString() } }));
}

test("the wallet resells a ticket at most at face value and then hides its QR", async ({ page, context, baseURL }) => {
  await signIn(context, baseURL!);
  let current = detail();
  await wallet(context, () => current);
  let body: unknown = null;
  await context.route(/\/api\/v1\/tickets\/[^/]+\/listings$/, async (route) => {
    body = route.request().postDataJSON();
    current = detail({ status: "Listed", resale: { ...detail().resale, canChange: false, listing: { listingId: "l1", price: 1_500_000, status: "Active", waitlistUntil: null } } });
    await route.fulfill({ json: { listingId: "l1" } });
  });

  await page.goto(`/tickets/${TICKET}`);
  await page.getByRole("button", { name: "Tuỳ chọn vé" }).click();
  await page.getByRole("menuitem", { name: "Bán lại" }).click();
  const sheet = page.getByRole("dialog", { name: "Bán lại vé" });
  await expect(sheet).toBeVisible();
  const price = sheet.getByRole("spinbutton", { name: "Giá bán" });
  await expect(price).toHaveValue("2000000");
  await price.fill("2500000");
  await expect(sheet.getByRole("button", { name: "Đăng bán" })).toBeDisabled();
  await price.fill("1500000");
  await expect(sheet.getByText(/Bạn nhận: 1\.500\.000/)).toBeVisible();
  await expectNoSeriousA11yIssues(page);
  await sheet.getByRole("button", { name: "Đăng bán" }).click();

  await expect(sheet).toBeHidden();
  await expect(page.getByRole("heading", { name: "Đang rao bán" })).toBeVisible();
  await expect(page.getByText(/Đang bán lại với giá 1\.500\.000/)).toBeVisible();
  expect(body).toEqual({ price: 1_500_000 });
});

test("the wallet sends a ticket to a friend with the warning that it stops working", async ({ page, context, baseURL }) => {
  await signIn(context, baseURL!);
  let current = detail();
  await wallet(context, () => current);
  let body: unknown = null;
  await context.route(/\/api\/v1\/tickets\/[^/]+\/transfers$/, async (route) => {
    body = route.request().postDataJSON();
    const expiresAt = new Date(Date.now() + 48 * 60 * MIN).toISOString();
    current = detail({ status: "Listed", resale: { ...detail().resale, canChange: false, transfer: { transferId: "x1", toEmail: "ban@example.com", expiresAt } } });
    await route.fulfill({ json: { transferId: "x1" } });
  });

  await page.goto(`/tickets/${TICKET}`);
  await page.getByRole("button", { name: "Tuỳ chọn vé" }).click();
  await page.getByRole("menuitem", { name: "Chuyển cho bạn bè" }).click();
  const dialog = page.getByRole("dialog", { name: "Chuyển cho bạn bè" });
  await expect(dialog.getByText("Vé hiện tại sẽ mất hiệu lực sau khi người nhận chấp nhận")).toBeVisible();
  await dialog.getByLabel("Email người nhận").fill("ban@example.com");
  await expectNoSeriousA11yIssues(page);
  await dialog.getByRole("button", { name: "Gửi vé" }).click();

  await expect(page.getByRole("heading", { name: "Đang chuyển vé" })).toBeVisible();
  expect(body).toEqual({ toEmail: "ban@example.com" });
});

test("a sold-out event lists resale tickets and Mua continues in checkout", async ({ page, context, baseURL }) => {
  await signIn(context, baseURL!);
  await context.route(/\/api\/v1\/events\/[^/]+\/availability$/, (route) =>
    route.fulfill({ json: { serverNow: new Date().toISOString(), version: 0, unavailable: [], tiers: [{ tierId: "t-sold-out", remaining: 0 }] } }),
  );
  await context.route(/\/api\/v1\/events\/[^/]+\/waitlist\/me$/, (route) =>
    route.fulfill({ status: 404, contentType: "application/problem+json", body: JSON.stringify({ status: 404, code: "waitlist.not_joined" }) }),
  );
  await context.route(/\/api\/v1\/events\/[^/]+\/listings$/, (route) =>
    route.fulfill({
      json: [{ tierId: "vip", tierName: "VIP", color: "#7c3aed", faceValue: 2_000_000, listings: [{ listingId: "l1", seatLabel: "A · 1 · 6", price: 1_800_000, currency: "VND" }] }],
    }),
  );
  let reserved = false;
  await context.route(/\/api\/v1\/listings\/l1\/reserve$/, async (route) => {
    reserved = route.request().headers()["idempotency-key"] !== undefined;
    await route.fulfill({ json: { id: "o1" } });
  });

  await page.goto("/events/son-tung-mtp-live");
  const section = page.locator("section:visible", { has: page.getByRole("heading", { name: "Vé bán lại" }) }).first();
  await expect(section).toBeVisible();
  await expect(section.getByText("Ghế A · 1 · 6")).toBeVisible();
  await expectNoSeriousA11yIssues(page);
  await section.getByRole("button", { name: "Mua" }).click();

  await expect(page).toHaveURL(/\/checkout\/o1$/);
  expect(reserved).toBe(true);
});

test("the recipient accepts a transfer and opens the new ticket", async ({ page, context, baseURL }) => {
  await signIn(context, baseURL!);
  const invite = (over: object = {}) => ({
    transferId: "x1",
    status: "Pending",
    expiresAt: new Date(Date.now() + 47 * 60 * MIN).toISOString(),
    fromName: "Trần Bình",
    eventId: "e1",
    eventSlug: "son-tung-mtp-live",
    eventTitle: "Sơn Tùng M-TP Live",
    startsAt: "2099-12-20T13:00:00Z",
    venue: "SVĐ Mỹ Đình",
    tierName: "VIP",
    seatLabel: "A · 1 · 6",
    newTicketId: null,
    ...over,
  });
  await context.route(/\/api\/v1\/transfers\/x1$/, (route) => route.fulfill({ json: invite() }));
  await context.route(/\/api\/v1\/transfers\/x1\/accept$/, (route) => route.fulfill({ json: invite({ status: "Accepted", newTicketId: TICKET }) }));

  await page.goto("/transfers/x1");
  await expect(page.getByRole("heading", { level: 1, name: "Bạn được tặng một vé" })).toBeVisible();
  await expect(page.getByText("Trần Bình chuyển cho bạn vé này.")).toBeVisible();
  await expectNoSeriousA11yIssues(page);
  await page.getByRole("button", { name: "Nhận vé" }).click();

  await expect(page.getByRole("heading", { level: 1, name: "Vé đã là của bạn" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Xem vé" })).toHaveAttribute("href", `/tickets/${TICKET}`);
});

test("a visitor opening a transfer link is sent to login and back", async ({ page }) => {
  await page.goto("/transfers/x1");
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Ftransfers%2Fx1$/);
});
