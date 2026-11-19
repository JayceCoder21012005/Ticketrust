import AxeBuilder from "@axe-core/playwright";
import { devices, expect, test, type BrowserContext } from "@playwright/test";

/** The account hub (UI refresh) against a scripted api: overview, navigation, phones, accessibility. */
const MIN = 60_000;

const session = {
  accessToken: "e2e-hub-token",
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

const inTwoWeeks = new Date(Date.now() + 14 * 24 * 60 * MIN).toISOString();

async function arrange(context: BrowserContext, baseURL: string) {
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await context.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: session }));
  await context.route("**/api/v1/me", (route) => route.fulfill({ json: session.user }));
  await context.route("**/api/v1/me/tickets**", (route) =>
    route.fulfill({
      json: {
        events: [
          {
            eventId: "e1",
            slug: "son-tung-mtp-live",
            title: "Sơn Tùng M-TP Live",
            coverImageUrl: null,
            startsAt: inTwoWeeks,
            endsAt: inTwoWeeks,
            venueName: "Sân vận động Mỹ Đình",
            city: "Hà Nội",
            status: "Published",
            tickets: [{ id: "t-1", orderId: "o-1", tierName: "VIP", seat: null, status: "Valid", holderName: "Nguyễn An" }],
          },
        ],
      },
    }),
  );
  await context.route("**/api/v1/me/orders**", (route) =>
    route.fulfill({
      json: {
        items: [
          { id: "o-1", eventId: "e1", eventTitle: "Sơn Tùng M-TP Live", eventSlug: "son-tung-mtp-live", eventStartsAt: inTwoWeeks, status: "Paid", total: 3_000_000, itemCount: 1, createdAt: "2026-11-01T03:00:00Z", paidAt: "2026-11-01T03:01:00Z" },
        ],
        nextCursor: null,
      },
    }),
  );
  await context.route("**/api/v1/me/follows", (route) => route.fulfill({ json: [] }));
}

test("the overview leads with the next event and opens its ticket", async ({ context, page, baseURL }) => {
  await arrange(context, baseURL!);
  await page.goto("/account");

  await expect(page.getByRole("heading", { level: 1, name: "Xin chào, Nguyễn An" })).toBeVisible();
  const nav = page.getByRole("navigation", { name: "Tài khoản" });
  await expect(nav.getByRole("link", { name: "Tổng quan" })).toHaveAttribute("aria-current", "page");
  const next = page.getByRole("region", { name: "Sự kiện sắp tới của bạn" });
  await expect(next.getByRole("heading", { name: "Sơn Tùng M-TP Live" })).toBeVisible();
  await expect(next.getByRole("link", { name: "Mở vé" })).toHaveAttribute("href", "/tickets/t-1");
  await expect(page.getByRole("region", { name: "Đơn hàng gần đây" }).getByRole("link", { name: "Sơn Tùng M-TP Live" })).toBeVisible();
  await expect(page.getByText("Bạn chưa theo dõi sự kiện nào.")).toBeVisible();

  await nav.getByRole("link", { name: "Hồ sơ" }).click();
  await expect(page).toHaveURL("/account/profile");
  await expect(page.getByRole("textbox", { name: "Họ và tên" })).toHaveValue("Nguyễn An");
});

for (const colorScheme of ["light", "dark"] as const) {
  test(`the account hub has no serious accessibility violations (${colorScheme})`, async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!);
    await page.emulateMedia({ colorScheme });
    await page.goto("/account");
    await expect(page.getByRole("region", { name: "Sự kiện sắp tới của bạn" }).getByRole("link", { name: "Mở vé" })).toBeVisible();
    await page.waitForFunction(() => document.getAnimations().length === 0);

    const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
    expect(violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
}

test.describe("phone", () => {
  test.use({ viewport: devices["Pixel 7"].viewport, hasTouch: true, isMobile: true });

  test("the hub's tabs never make the page scroll sideways", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!);
    await page.goto("/account");
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();

    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width);
  });
});
