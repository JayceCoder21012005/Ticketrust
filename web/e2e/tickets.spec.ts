import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";

/**
 * `/account/tickets` (spec 10) against a scripted api: the same `context.route` pattern as orders.spec.ts. The real
 * endpoint is covered by the .NET integration tests; the wallet screen (`/tickets/[id]`) has its own spec.
 */
const MIN = 60_000;

const user = {
  accessToken: "e2e-buyer-token",
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

const ticketId = (n: number) => `0000000${n}-aaaa-bbbb-cccc-dddddddddddd`;

const ticket = (n: number, tierName = "Hạng VIP") => ({
  id: ticketId(n),
  orderId: "o1",
  tierName,
  seat: { section: "A", row: "1", number: String(n) },
  status: "Valid",
  holderName: "Nguyễn An",
});

const group = (n: number, tickets: ReturnType<typeof ticket>[], extra: Record<string, unknown> = {}) => ({
  eventId: `e${n}`,
  slug: `dem-nhac-${n}`,
  title: `Đêm nhạc số ${n}`,
  coverImageUrl: null,
  startsAt: "2099-12-20T13:00:00Z",
  endsAt: "2099-12-20T16:00:00Z",
  venueName: "Nhà hát Hòa Bình",
  city: "TP.HCM",
  status: "Published",
  tickets,
  ...extra,
});

const ended = { startsAt: "2020-03-01T13:00:00Z", endsAt: "2020-03-01T16:00:00Z", status: "Ended" };

type Wallet = Record<"upcoming" | "past", ReturnType<typeof group>[]>;

const wallet: Wallet = {
  upcoming: [group(1, [ticket(1)]), group(2, [ticket(2), ticket(3, "Hạng thường"), ticket(4)])],
  past: [group(5, [ticket(5)], ended), group(6, [ticket(6)], { ...ended, status: "Cancelled" })],
};

/** Signs the visitor in and scripts `GET /me/tickets?scope=…`; `calls` records the scopes asked for. */
async function arrange(context: BrowserContext, baseURL: string, tickets: Wallet = wallet) {
  const calls: string[] = [];
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await context.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: user }));
  await context.route(/\/api\/v1\/me\/tickets(\?.*)?$/, (route) => {
    const scope = new URL(route.request().url()).searchParams.get("scope") as keyof Wallet;
    calls.push(scope);
    return route.fulfill({ json: { events: tickets[scope] ?? [] } });
  });
  // The shared header lists the inbox; it is not what these tests are about.
  await context.route(/\/api\/v1\/me\/notifications(\?.*)?$/, (route) => route.fulfill({ json: { items: [], nextCursor: null, unreadCount: 0 } }));
  return calls;
}

const empty: Wallet = { upcoming: [], past: [] };

test.describe("my tickets", () => {
  test("lists the upcoming events with date, venue, ticket count and a link to the first ticket", async ({ context, page, baseURL }) => {
    const calls = await arrange(context, baseURL!);
    await page.goto("/account/tickets");

    await expect(page.getByRole("heading", { level: 1, name: "Vé của tôi" })).toBeFocused();
    const cards = page.getByRole("listitem");
    await expect(cards).toHaveCount(2);
    await expect(cards.nth(0)).toContainText("Đêm nhạc số 1");
    await expect(cards.nth(0)).toContainText("20:00 20/12/2099");
    await expect(cards.nth(0)).toContainText("Nhà hát Hòa Bình, TP.HCM");
    await expect(cards.nth(0)).toContainText("1 vé");
    await expect(cards.nth(0).getByRole("link", { name: "Đêm nhạc số 1" })).toHaveAttribute("href", `/tickets/${ticketId(1)}`);
    await expect(cards.nth(1)).toContainText("3 vé");
    await expect(cards.nth(1)).toContainText("Hạng VIP, Hạng thường");
    await expect(cards.nth(1).getByRole("link", { name: "Đêm nhạc số 2" })).toHaveAttribute("href", `/tickets/${ticketId(2)}`);
    expect(calls).toEqual(["upcoming"]);
  });

  test("switches to the past tab, keeps it in the url and on reload, and shows the status chips", async ({ context, page, baseURL }) => {
    const calls = await arrange(context, baseURL!);
    await page.goto("/account/tickets");
    await expect(page.getByRole("listitem")).toHaveCount(2);

    await page.getByRole("tab", { name: "Đã qua" }).click();

    await expect(page).toHaveURL(/\/account\/tickets\?scope=past$/);
    await expect(page.getByRole("tab", { name: "Đã qua" })).toHaveAttribute("aria-selected", "true");
    const cards = page.getByRole("listitem");
    await expect(cards).toHaveCount(2);
    await expect(cards.nth(0)).toContainText("Đêm nhạc số 5");
    await expect(cards.nth(0)).toContainText("Đã diễn ra");
    await expect(cards.nth(1)).toContainText("Đã huỷ");

    await page.reload();
    await expect(page.getByRole("tab", { name: "Đã qua" })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByRole("listitem")).toHaveCount(2);
    expect(calls).toEqual(["upcoming", "past", "past"]);

    await page.getByRole("tab", { name: "Sắp diễn ra" }).click();
    await expect(page).toHaveURL(/\/account\/tickets$/);
  });

  test("the tabs work with the arrow keys", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!);
    await page.goto("/account/tickets");
    await expect(page.getByRole("listitem")).toHaveCount(2);

    await page.getByRole("tab", { name: "Sắp diễn ra" }).focus();
    await page.keyboard.press("ArrowRight");

    await expect(page.getByRole("tab", { name: "Đã qua" })).toBeFocused();
    await expect(page).toHaveURL(/scope=past$/);
    await expect(page.getByRole("listitem").first()).toContainText("Đêm nhạc số 5");
  });

  test("shows an empty state that points to the events, on both tabs", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!, empty);
    await page.goto("/account/tickets");

    await expect(page.getByText("Bạn chưa có vé nào sắp diễn ra")).toBeVisible();
    await expect(page.getByRole("link", { name: "Khám phá sự kiện" })).toHaveAttribute("href", "/events");

    await page.getByRole("tab", { name: "Đã qua" }).click();
    await expect(page.getByText("Bạn chưa có vé nào ở các sự kiện đã qua")).toBeVisible();
  });

  test("a failed load offers a retry", async ({ context, page, baseURL }) => {
    await arrange(context, baseURL!);
    // The app client retries a failed query once, so the first load has to fail twice to show the error.
    let failures = 2;
    await context.route(/\/api\/v1\/me\/tickets(\?.*)?$/, (route) =>
      failures-- > 0 ? route.fulfill({ status: 500, json: {} }) : route.fallback(),
    );
    await page.goto("/account/tickets");

    await expect(page.getByText("Không tải được vé của bạn.")).toBeVisible();
    await page.getByRole("button", { name: "Thử lại" }).click();
    await expect(page.getByRole("listitem")).toHaveCount(2);
  });

  test("a signed-out visitor is sent to login", async ({ page }) => {
    await page.goto("/account/tickets?scope=past");
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Faccount%2Ftickets%3Fscope%3Dpast/);
  });

  test.describe("at 320 px", () => {
    test.use({ viewport: { width: 320, height: 640 } });

    const layouts: [string, Wallet][] = [
      ["a list with long names", { upcoming: [group(1, [ticket(1, "Hạng VIP Platinum Premium Experience Package"), ticket(2)], { title: "Đêm nhạc Giao hưởng Mùa Thu Hà Nội Đặc Biệt Kỷ Niệm Mười Năm", venueName: "Trung tâm Hội nghị Quốc gia Mỹ Đình Hà Nội" })], past: [] }],
      ["the empty state", empty],
    ];
    for (const [name, tickets] of layouts) {
      test(`${name} has no horizontal scroll`, async ({ context, page, baseURL }) => {
        await arrange(context, baseURL!, tickets);
        await page.goto("/account/tickets");
        await expect(page.getByRole("tab", { name: "Đã qua" })).toBeVisible();
        await expect(page.getByRole("listitem").or(page.getByText("Bạn chưa có vé nào sắp diễn ra")).first()).toBeVisible();

        const width = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, inner: window.innerWidth }));
        expect(width.scroll).toBeLessThanOrEqual(width.inner);
      });
    }
  });
});

type Scenario = { name: string; tickets: Wallet; open: (page: Page) => Promise<void> };

const scenarios: Scenario[] = [
  { name: "list", tickets: wallet, open: async (page) => { await page.goto("/account/tickets"); await expect(page.getByRole("listitem")).toHaveCount(2); } },
  { name: "past list with chips", tickets: wallet, open: async (page) => { await page.goto("/account/tickets?scope=past"); await expect(page.getByText("Đã huỷ")).toBeVisible(); } },
  { name: "empty state", tickets: empty, open: async (page) => { await page.goto("/account/tickets"); await expect(page.getByText("Bạn chưa có vé nào sắp diễn ra")).toBeVisible(); } },
];

for (const colorScheme of ["light", "dark"] as const) {
  for (const scenario of scenarios) {
    test(`${scenario.name} has no serious accessibility violations (${colorScheme})`, async ({ context, page, baseURL }) => {
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      await arrange(context, baseURL!, scenario.tickets);
      await scenario.open(page);

      const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
      const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
      expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target) })), null, 2)).toEqual([]);
    });
  }
}
