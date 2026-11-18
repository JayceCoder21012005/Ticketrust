import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";

/**
 * The gate screens against a scripted api (spec 11): the .NET integration tests cover the real scan rules. The camera is not
 * available in the test browser, so the scans here go through the manual entry; ZXing itself is covered by unit tests that
 * decode the QR codes the wallet draws.
 */
const EVENT_ID = "11111111-1111-4111-8111-111111111111";
const MIN = 60_000;

const staff = {
  accessToken: "e2e-gate-token",
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id: "g1", email: "gate@example.com", fullName: "Phạm Soát Vé", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["GateStaff"] },
};

const gateEvent = {
  eventId: EVENT_ID,
  slug: "son-tung-mtp-live",
  title: "Sơn Tùng M-TP Live",
  startsAt: new Date(Date.now() + 60 * MIN).toISOString(),
  endsAt: new Date(Date.now() + 240 * MIN).toISOString(),
  venueName: "SVĐ Mỹ Đình",
  city: "Hà Nội",
  gateName: "Cổng B",
};

async function arrange(context: BrowserContext, page: Page, baseURL: string, answers: object[], user = staff) {
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await page.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: user }));
  await context.route("**/api/v1/gate/events", (route) => route.fulfill({ json: [gateEvent] }));
  const state = { checkedIn: 10, scans: [] as string[] };
  await context.route("**/api/v1/gate/events/*/stats", (route) => route.fulfill({ json: { checkedIn: state.checkedIn, total: 50, lastMinute: 1 } }));
  await context.route("**/api/v1/gate/events/*/scan", (route) => {
    state.scans.push((route.request().postDataJSON() as { payload: string }).payload);
    const answer = answers[Math.min(state.scans.length - 1, answers.length - 1)] as { result: string };
    if (answer.result === "Admitted") state.checkedIn++;
    return route.fulfill({ json: answer });
  });
  return state;
}

const admitted = {
  result: "Admitted",
  reason: null,
  ticket: { tier: "VIP", section: "A", row: "3", seat: "12", holderName: "Lê Thị Hương" },
  checkedInAt: new Date().toISOString(),
  gateName: null,
};

async function axe(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  return violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
}

test("the gate lists the assigned events and opens the scanner", async ({ context, page, baseURL }) => {
  await arrange(context, page, baseURL!, [admitted]);

  await page.goto("/gate");
  await expect(page.getByRole("heading", { level: 1, name: "Soát vé" })).toBeVisible();
  await page.getByRole("link", { name: /Sơn Tùng M-TP Live/ }).click();

  await expect(page).toHaveURL(`/gate/${EVENT_ID}`);
  await expect(page.getByText("Cổng B").first()).toBeVisible();
  await expect(page.getByTestId("gate-counter")).toHaveText("10 / 50");
});

test("a scan answers green with the seat, then the counter goes up", async ({ context, page, baseURL }) => {
  const state = await arrange(context, page, baseURL!, [admitted]);
  await page.goto(`/gate/${EVENT_ID}`);

  // The page is server-rendered: wait for the manual entry to be live before typing.
  const code = page.getByRole("textbox", { name: "Mã vé" });
  await expect(page.getByTestId("gate-counter")).toHaveText("10 / 50");
  await code.fill("TR1.test.code");
  await page.getByRole("button", { name: "Kiểm tra" }).click();

  const overlay = page.getByTestId("scan-result");
  await expect(overlay).toContainText("Hợp lệ");
  await expect(overlay).toBeVisible();
  await expect(overlay).toContainText("Khu A · Hàng 3 · Ghế 12");
  await expect(overlay).toHaveAttribute("data-tone", "ok");
  expect(state.scans).toEqual(["TR1.test.code"]);
  await expect(overlay).toBeHidden({ timeout: 4000 }); // gone after 1.5 s
  await expect(page.getByTestId("gate-counter")).toHaveText("11 / 50");
});

test("an already checked-in ticket answers amber and a bad code red", async ({ context, page, baseURL }) => {
  await arrange(context, page, baseURL!, [
    { ...admitted, result: "AlreadyCheckedIn", gateName: "Cổng A", checkedInAt: "2026-11-20T12:42:00Z" },
    { result: "Invalid", reason: "bad_format", ticket: null, checkedInAt: null, gateName: null },
  ]);
  await page.goto(`/gate/${EVENT_ID}`);
  await expect(page.getByTestId("gate-counter")).toHaveText("10 / 50");

  await page.getByRole("textbox", { name: "Mã vé" }).fill("TR1.one");
  await page.getByRole("button", { name: "Kiểm tra" }).click();
  await expect(page.getByTestId("scan-result")).toContainText("Đã check-in lúc 19:42 – Cổng A");
  await expect(page.getByTestId("scan-result")).toHaveAttribute("data-tone", "warn");
  await page.getByRole("button", { name: "Đóng" }).click(); // closes it early
  await expect(page.getByTestId("scan-result")).toBeHidden();

  await page.getByRole("textbox", { name: "Mã vé" }).fill("not-a-ticket");
  await page.getByRole("button", { name: "Kiểm tra" }).click();
  await expect(page.getByTestId("scan-result")).toContainText("Mã không hợp lệ");
  await expect(page.getByTestId("scan-result")).toHaveAttribute("data-tone", "bad");
});

test("when the api is unreachable the screen says so", async ({ context, page, baseURL }) => {
  await arrange(context, page, baseURL!, [admitted]);
  await context.route("**/api/v1/gate/events/*/stats", (route) => route.abort("internetdisconnected"));
  await page.goto(`/gate/${EVENT_ID}`);

  await expect(page.getByRole("alert").filter({ hasText: "Mất kết nối" })).toBeVisible();
});

test("a customer is told this area is for gate staff", async ({ context, page, baseURL }) => {
  await arrange(context, page, baseURL!, [admitted], { ...staff, user: { ...staff.user, roles: ["Customer"] } });

  await page.goto("/gate");

  await expect(page.getByRole("heading", { name: "Bạn cần tài khoản nhân viên soát vé" })).toBeVisible();
});

for (const colorScheme of ["light", "dark"] as const) {
  test(`the scanner has no serious accessibility violations (${colorScheme})`, async ({ context, page, baseURL }) => {
    await arrange(context, page, baseURL!, [admitted]);
    await page.emulateMedia({ colorScheme });
    await page.goto(`/gate/${EVENT_ID}`);
    await expect(page.getByTestId("gate-counter")).toHaveText("10 / 50");
    await page.waitForFunction(() => document.getAnimations().length === 0);

    expect(await axe(page)).toEqual([]);
  });
}
