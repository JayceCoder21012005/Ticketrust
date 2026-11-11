import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";

/**
 * The "Nhắc tôi" button of the event page (spec 09) against a scripted follow endpoint: the .NET integration tests cover the
 * real one. Same `context.route` pattern as notifications.spec.ts. The event is one whose sale has not opened yet (the
 * button is part of that CTA state); the mock api (port 3199) tells us its GUID.
 */
const SLUG = "ha-anh-tuan-chan-troi-ruc-ro";
const EVENT_PATH = `/events/${SLUG}`;
const MOCK_API = "http://localhost:3199";
const MIN = 60_000;

const user = {
  accessToken: "e2e-buyer-token",
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

type Follow = { following: boolean; calls: string[]; ids: Set<string>; failNext: { status: number; code: string } | null };

/** Signs the visitor in (hint cookie + refresh) and scripts `/events/{id}/follow`; the state lives in the route handler. */
async function arrange(context: BrowserContext, baseURL: string, { following = false }: { following?: boolean } = {}) {
  const follow: Follow = { following, calls: [], ids: new Set(), failNext: null };
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await context.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: user }));
  await context.route(/\/api\/v1\/me\/notifications(\?.*)?$/, (route) => route.fulfill({ json: { items: [], nextCursor: null, unreadCount: 0 } }));
  await context.route(/\/api\/v1\/events\/[^/]+\/follow$/, async (route) => {
    const request = route.request();
    follow.calls.push(request.method());
    follow.ids.add(new URL(request.url()).pathname.split("/").at(-2)!);
    if (request.method() === "GET") return route.fulfill({ json: { following: follow.following } });
    if (follow.failNext) {
      const { status, code } = follow.failNext;
      follow.failNext = null;
      return route.fulfill({ status, contentType: "application/problem+json", body: JSON.stringify({ status, code, title: code }) });
    }

    follow.following = request.method() === "POST";
    return route.fulfill({ status: 204 });
  });
  return follow;
}

async function eventId(page: Page): Promise<string> {
  const response = await page.request.get(`${MOCK_API}/api/v1/events/${SLUG}`);
  return ((await response.json()) as { id: string }).id;
}

// The panel (desktop) and the sticky bar (mobile) both render the button; the viewport shows one of them.
const remind = (page: Page) => page.locator("button:visible", { hasText: "Nhắc tôi" });
const reminding = (page: Page) => page.locator("button:visible", { hasText: "Đã bật nhắc nhở" });

test("an anonymous visitor gets a link to login that brings them back to the event", async ({ page }) => {
  await page.goto(EVENT_PATH);

  const link = page.getByRole("link", { name: "Nhắc tôi" }).locator("visible=true");
  await expect(link).toBeVisible();
  await link.click();

  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fevents%2Fha-anh-tuan-chan-troi-ruc-ro$/);
});

test("follow, reload keeps the state, unfollow", async ({ context, page, baseURL }) => {
  const follow = await arrange(context, baseURL!);
  await page.goto(EVENT_PATH);

  await expect(remind(page)).toBeEnabled();
  await expect(remind(page)).toHaveAttribute("aria-pressed", "false");
  await remind(page).click();
  await expect(reminding(page)).toHaveAttribute("aria-pressed", "true");
  await expect.poll(() => follow.following).toBe(true);

  await page.reload();
  await expect(reminding(page)).toBeEnabled();
  await expect(reminding(page)).toHaveAttribute("aria-pressed", "true");

  await reminding(page).click();
  await expect(remind(page)).toHaveAttribute("aria-pressed", "false");
  await expect.poll(() => follow.following).toBe(false);

  await page.reload();
  await expect(remind(page)).toBeEnabled();
  await expect(remind(page)).toHaveAttribute("aria-pressed", "false");
  expect(follow.calls.filter((method) => method !== "GET")).toEqual(["POST", "DELETE"]);
  expect([...follow.ids]).toEqual([await eventId(page)]);
});

test("is operable by keyboard", async ({ context, page, baseURL }) => {
  const follow = await arrange(context, baseURL!);
  await page.goto(EVENT_PATH);

  await expect(remind(page)).toBeEnabled();
  await remind(page).focus();
  await page.keyboard.press("Enter");

  await expect(reminding(page)).toBeFocused();
  await expect.poll(() => follow.following).toBe(true);
});

test("puts the button back and says so when the api refuses", async ({ context, page, baseURL }) => {
  const follow = await arrange(context, baseURL!);
  follow.failNext = { status: 503, code: "system.busy" };
  await page.goto(EVENT_PATH);

  await expect(remind(page)).toBeEnabled();
  await remind(page).click();

  await expect(page.getByRole("alert").filter({ hasText: "Hệ thống đang bận" }).locator("visible=true")).toBeVisible();
  await expect(remind(page)).toHaveAttribute("aria-pressed", "false");
  await expect(remind(page)).toBeEnabled();
});

test("an event that can no longer be followed disables the button and says why", async ({ context, page, baseURL }) => {
  const follow = await arrange(context, baseURL!);
  follow.failNext = { status: 409, code: "event.not_followable" };
  await page.goto(EVENT_PATH);

  await expect(remind(page)).toBeEnabled();
  await remind(page).click();

  await expect(page.getByRole("alert").filter({ hasText: "không còn nhận nhắc nhở" }).locator("visible=true")).toBeVisible();
  await expect(remind(page)).toBeDisabled();
});

for (const colorScheme of ["light", "dark"] as const) {
  for (const state of ["signed out", "not following", "following", "refused"] as const) {
    test(`the event page has no serious accessibility violations with the button ${state} (${colorScheme})`, async ({ context, page, baseURL }) => {
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      if (state !== "signed out") {
        const follow = await arrange(context, baseURL!, { following: state === "following" });
        if (state === "refused") follow.failNext = { status: 409, code: "event.not_followable" };
      }

      await page.goto(EVENT_PATH);
      if (state === "signed out") {
        await expect(page.getByRole("link", { name: "Nhắc tôi" }).locator("visible=true")).toBeVisible();
      } else if (state === "following") {
        await expect(reminding(page)).toBeEnabled();
      } else {
        await expect(remind(page)).toBeEnabled();
      }

      if (state === "refused") {
        await remind(page).click();
        await expect(page.getByRole("alert").filter({ hasText: "không còn nhận nhắc nhở" }).locator("visible=true")).toBeVisible();
      }

      const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
      const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
      expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target) })), null, 2)).toEqual([]);
    });
  }
}
