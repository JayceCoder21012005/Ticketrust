import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import { base64ToBytes, qrPayload } from "../src/lib/tickets/qr-code";
import { encodeQr } from "../src/lib/tickets/qr-matrix";
import { qrPath } from "../src/lib/tickets/qr-svg";
import { formatClock } from "../src/lib/format";

/**
 * `/account/tickets` (spec 10) against a scripted api: the same `context.route` pattern as orders.spec.ts. The real
 * endpoint is covered by the .NET integration tests. The wallet screen (`/tickets/[id]`) is at the end.
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

/* ---------------------------------------------------------------------------------------------------------------------
 * `/tickets/[id]`: the wallet screen with the rotating QR. The expected QR is computed here with the app's own encoder
 * and code generator (`qrPayload` → `encodeQr` → `qrPath`), so the test proves the page draws a real TR1 code for the
 * right 15-second step without the page exposing the code anywhere.
 * ------------------------------------------------------------------------------------------------------------------ */

const orderIds = [ticketId(1), ticketId(2), ticketId(3), ticketId(4)];
const SECRET = Buffer.from(Array.from({ length: 32 }, (_, i) => i * 7)).toString("base64");

const detail = (n: number, extra: Record<string, unknown> = {}, event: Record<string, unknown> = {}) => ({
  ...ticket(n),
  orderTicketIds: orderIds,
  event: {
    eventId: "e2",
    slug: "dem-nhac-so-2",
    title: "Đêm nhạc số 2",
    coverImageUrl: null,
    startsAt: "2099-12-20T13:00:00Z",
    endsAt: "2099-12-20T16:00:00Z",
    venueName: "Nhà hát Hòa Bình",
    city: "TP.HCM",
    status: "Published",
    ...event,
  },
  ...extra,
});

type WalletApi = { details?: Record<string, ReturnType<typeof detail>>; serverNow?: () => Promise<string> };

/** An api clock that reads the page's (fake) clock when asked, `skewMs` ahead of it. */
const browserNow = (page: Page, skewMs = 0) => async () => new Date((await page.evaluate(() => Date.now())) + skewMs).toISOString();

/** Signs in and scripts the ticket, its qr-key and the api clock; returns the ids whose qr-key was asked for. */
async function arrangeWallet(context: BrowserContext, baseURL: string, { details = {}, serverNow }: WalletApi = {}) {
  await arrange(context, baseURL);
  const keyCalls: string[] = [];
  const find = (id: string) => details[id] ?? detail(orderIds.indexOf(id) + 1);
  await context.route(/\/api\/v1\/tickets\/[^/?]+$/, (route) => {
    const id = new URL(route.request().url()).pathname.split("/").at(-1)!;
    return route.fulfill({ json: find(id) });
  });
  await context.route(/\/api\/v1\/tickets\/[^/?]+\/qr-key$/, (route) => {
    const id = new URL(route.request().url()).pathname.split("/").at(-2)!;
    keyCalls.push(id);
    return route.fulfill({ json: { ticketId: id, secret: SECRET, stepSeconds: 15, format: "TR1" }, headers: { "cache-control": "no-store" } });
  });
  if (serverNow) await context.route(/\/api\/v1\/time$/, async (route) => route.fulfill({ json: { serverNow: await serverNow() } }));
  return keyCalls;
}

const qrImage = (page: Page) => page.getByRole("img", { name: "Mã QR vào cửa, đổi mỗi 15 giây" });

/** The path the wallet must draw for `id` at `unixSeconds`, computed with the app's own modules. */
async function expectedPath(id: string, unixSeconds: number) {
  const payload = await qrPayload(id, base64ToBytes(SECRET), unixSeconds);
  expect(payload).toMatch(new RegExp(`^TR1\\.[A-Za-z0-9_-]{22}\\.${Math.floor(unixSeconds / 15)}\\.[A-Za-z0-9_-]{14}$`));
  return qrPath(encodeQr(payload));
}

/** Opens the wallet and waits for the QR and for the api clock answer (the offset is applied from then on). */
async function openWallet(page: Page, id: string) {
  const time = page.waitForResponse(/\/api\/v1\/time$/);
  await page.goto(`/tickets/${id}`);
  await time;
  await expect(page.getByRole("heading", { level: 1 })).toBeFocused();
  await expect(qrImage(page)).toBeVisible();
}

/**
 * Freezes the page's clock 5 s into the next 15-second step and returns that step's start (device time). A jump fires
 * each due timer only once, at its own time, so the last second is run normally for the clock's tick to land on 5 s.
 */
async function freezeFiveSecondsIntoAStep(page: Page): Promise<number> {
  const now = await page.evaluate(() => Date.now());
  const step = Math.ceil((now + 1_000) / 15_000) * 15_000;
  await page.clock.pauseAt(step + 4_500);
  await page.clock.runFor(1_000);
  return step;
}

test.describe("ticket wallet", () => {
  test("draws a real TR1 code that changes at the 15-second boundary, with the live clock", async ({ context, page, baseURL }) => {
    await page.clock.install({ time: Date.UTC(2026, 10, 12, 12, 0, 0) });
    // The api clock agrees with the (fake) device clock.
    const keyCalls = await arrangeWallet(context, baseURL!, { serverNow: browserNow(page) });

    await openWallet(page, ticketId(2));
    const step = await freezeFiveSecondsIntoAStep(page);

    const path = qrImage(page).locator("path[data-modules]");
    const first = await expectedPath(ticketId(2), (step + 5_000) / 1000);
    await expect(path).toHaveAttribute("d", first);
    await expect(page.getByRole("timer")).toHaveText(formatClock(step + 5_000));
    await expect(page.getByRole("progressbar", { name: "Thời gian đến mã mới" })).toHaveAttribute("aria-valuenow", "10");
    // Neither the secret nor the code is ever text in the page.
    expect(await page.content()).not.toContain(SECRET);
    expect(await page.locator("body").innerText()).not.toMatch(/TR1\./);

    await page.clock.runFor(15_000);
    const next = await expectedPath(ticketId(2), (step + 20_000) / 1000);
    expect(next).not.toBe(first);
    await expect(path).toHaveAttribute("d", next);
    await expect(page.getByRole("timer")).toHaveText(formatClock(step + 20_000));
    expect(keyCalls).toEqual([ticketId(2)]);

    // Seat info in large type, holder, tier, the brightness tip and the calendar button.
    const seat = page.getByRole("region", { name: "Thông tin chỗ ngồi" });
    await expect(seat).toContainText("Khu");
    await expect(seat).toContainText("Hàng");
    await expect(seat).toContainText("Ghế");
    await expect(seat).toContainText("Nguyễn An");
    await expect(page.getByText("Tăng độ sáng màn hình để quét mã nhanh hơn.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Thêm vào lịch" })).toBeVisible();
  });

  test("runs the code on the server clock when the device clock is off", async ({ context, page, baseURL }) => {
    // The device is 2 minutes slow (a whole number of steps, so both grids line up); the api clock is the truth (C10).
    const skew = 120_000;
    await page.clock.install({ time: Date.UTC(2026, 10, 12, 12, 0, 0) });
    await arrangeWallet(context, baseURL!, { serverNow: browserNow(page, skew) });

    await openWallet(page, ticketId(2));
    const step = await freezeFiveSecondsIntoAStep(page);

    await expect(qrImage(page).locator("path[data-modules]")).toHaveAttribute("d", await expectedPath(ticketId(2), (step + skew + 5_000) / 1000));
    await expect(page.getByRole("timer")).toHaveText(formatClock(step + skew + 5_000));
  });

  test("downloads an .ics for the calendar", async ({ context, page, baseURL }) => {
    await arrangeWallet(context, baseURL!);
    await page.goto(`/tickets/${ticketId(2)}`);

    const download = page.waitForEvent("download");
    await page.getByRole("button", { name: "Thêm vào lịch" }).click();
    const file = await download;
    expect(file.suggestedFilename()).toBe("dem-nhac-so-2.ics");
    const text = readFileSync((await file.path())!, "utf8");
    expect(text).toContain("BEGIN:VEVENT\r\n");
    expect(text).toContain("DTSTART:20991220T130000Z");
    expect(text).toContain("SUMMARY:Đêm nhạc số 2");
  });

  const overlays: [string, Record<string, unknown>, Record<string, unknown>, string][] = [
    ["a listed ticket", { status: "Listed" }, {}, "Đang rao bán"],
    ["a checked-in ticket", { status: "CheckedIn" }, {}, "Đã check-in"],
    ["a voided ticket", { status: "Voided" }, {}, "Đã huỷ"],
    ["a ticket of a cancelled event", {}, { status: "Cancelled" }, "Đã huỷ"],
    ["a ticket of an event that has ended", {}, { status: "Ended", startsAt: "2020-03-01T13:00:00Z", endsAt: "2020-03-01T16:00:00Z" }, "Sự kiện đã kết thúc"],
  ];
  for (const [name, extra, event, title] of overlays) {
    test(`${name} shows "${title}" instead of the QR and never asks for its key`, async ({ context, page, baseURL }) => {
      const keyCalls = await arrangeWallet(context, baseURL!, { details: { [ticketId(2)]: detail(2, extra, event) } });
      await page.goto(`/tickets/${ticketId(2)}`);

      await expect(page.getByRole("heading", { level: 2, name: title })).toBeVisible();
      await expect(qrImage(page)).toHaveCount(0);
      await expect(page.getByRole("timer")).toHaveCount(0);
      expect(keyCalls).toEqual([]);
    });
  }

  test("moves between the order's tickets with the buttons and the arrow keys", async ({ context, page, baseURL }) => {
    await arrangeWallet(context, baseURL!);
    await page.goto(`/tickets/${ticketId(2)}`);
    await expect(page.getByText("2/4")).toBeVisible();
    await expect(page.getByRole("heading", { level: 1 })).toBeFocused();

    await page.keyboard.press("ArrowRight");
    await expect(page).toHaveURL(new RegExp(`/tickets/${ticketId(3)}$`));
    await expect(page.getByText("3/4")).toBeVisible();
    await expect(page.getByRole("heading", { level: 1 })).toBeFocused();

    await page.keyboard.press("ArrowLeft");
    await expect(page.getByText("2/4")).toBeVisible();
    await expect(page.getByRole("heading", { level: 1 })).toBeFocused();
    await page.keyboard.press("ArrowLeft");
    await expect(page).toHaveURL(new RegExp(`/tickets/${ticketId(1)}$`));
    await expect(page.getByText("1/4")).toBeVisible();
    await expect(page.getByRole("button", { name: "Vé trước" })).toBeDisabled();

    // The buttons are in the tab order and work with the keyboard too.
    await page.getByRole("link", { name: "Vé sau" }).focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(new RegExp(`/tickets/${ticketId(2)}$`));
    await expect(qrImage(page)).toBeVisible();
  });

  test("an unknown ticket shows the not-found state", async ({ context, page, baseURL }) => {
    await arrangeWallet(context, baseURL!);
    await context.route(/\/api\/v1\/tickets\/[^/?]+$/, (route) => route.fulfill({ status: 404, json: { code: "ticket.not_found", status: 404 } }));
    await page.goto(`/tickets/${ticketId(9)}`);

    await expect(page.getByRole("heading", { name: "Không tìm thấy vé" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Về vé của tôi" })).toHaveAttribute("href", "/account/tickets");
  });

  test("a signed-out visitor is sent to login", async ({ page }) => {
    await page.goto(`/tickets/${ticketId(2)}`);
    await expect(page).toHaveURL(new RegExp(`/login\\?returnUrl=%2Ftickets%2F${ticketId(2)}`));
  });

  test.describe("at 320 px", () => {
    test.use({ viewport: { width: 320, height: 640 } });

    test("has no horizontal scroll, even with long names", async ({ context, page, baseURL }) => {
      const long = detail(
        2,
        { tierName: "Hạng VIP Platinum Premium Experience Package", holderName: "Nguyễn Thị Thanh Hương Ngọc Bích" },
        { title: "Đêm nhạc Giao hưởng Mùa Thu Hà Nội Đặc Biệt Kỷ Niệm Mười Năm", venueName: "Trung tâm Hội nghị Quốc gia Mỹ Đình Hà Nội" },
      );
      await arrangeWallet(context, baseURL!, { details: { [ticketId(2)]: long } });
      await page.goto(`/tickets/${ticketId(2)}`);
      await expect(qrImage(page)).toBeVisible();

      const width = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, inner: window.innerWidth }));
      expect(width.scroll).toBeLessThanOrEqual(width.inner);
      // The QR uses the width it has (320 px minus the gutters).
      const box = await qrImage(page).boundingBox();
      expect(box!.width).toBeGreaterThanOrEqual(270);
    });
  });

  const a11yScenarios: [string, WalletApi["details"]][] = [
    ["with the QR", {}],
    ["with an overlay", { [ticketId(2)]: detail(2, { status: "Listed" }) }],
  ];
  for (const colorScheme of ["light", "dark"] as const) {
    for (const [name, details] of a11yScenarios) {
      test(`the wallet ${name} has no serious accessibility violations (${colorScheme})`, async ({ context, page, baseURL }) => {
        await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
        await arrangeWallet(context, baseURL!, { details });
        await page.goto(`/tickets/${ticketId(2)}`);
        await expect(qrImage(page).or(page.getByRole("heading", { level: 2, name: "Đang rao bán" }))).toBeVisible();

        const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
        const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
        expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target) })), null, 2)).toEqual([]);
      });
    }
  }
});
