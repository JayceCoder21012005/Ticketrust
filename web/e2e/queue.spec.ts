import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";

/**
 * The waiting room against a scripted api (spec 06): the .NET integration tests cover the real one. Routes are set
 * on the context, because the polling runs inside a Web Worker and its requests do not belong to the page.
 */
const SLUG = "son-tung-mtp-live";
const MIN = 60_000;

const user = {
  accessToken: "e2e-buyer-token",
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

type Body = Record<string, unknown>;

const base = (): Body => ({
  status: "Waiting",
  position: 3,
  aheadOfYou: 2,
  estimatedWaitSeconds: 4,
  opensAt: new Date(Date.now() - MIN).toISOString(),
  serverNow: new Date().toISOString(),
  pollAfterMs: 500,
  admissionToken: null,
  tokenExpiresAt: null,
});

const waiting = (position: number): Body => ({ ...base(), position, aheadOfYou: position - 1 });
const admitted = (): Body => ({
  ...base(),
  status: "Admitted",
  position: null,
  aheadOfYou: null,
  estimatedWaitSeconds: null,
  pollAfterMs: 30_000,
  admissionToken: "adm-token",
  tokenExpiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
});

/** Signs in (restored session) and scripts join + me; `script` is consumed one answer per `me` call, the last repeats. */
async function arrange(context: BrowserContext, page: Page, baseURL: string, script: () => Body[], opts: { joinStatus?: number; joinBody?: Body } = {}) {
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  await page.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: user }));

  const answers = script();
  const state = { joins: 0, polls: 0, authHeaders: [] as string[] };
  await context.route("**/api/v1/events/*/queue/join", (route) => {
    state.joins++;
    return route.fulfill({ status: opts.joinStatus ?? 200, json: opts.joinBody ?? answers[0] });
  });
  await context.route("**/api/v1/events/*/queue/me", (route) => {
    state.authHeaders.push(route.request().headers()["authorization"] ?? "");
    const answer = answers[Math.min(state.polls++, answers.length - 1)];
    return route.fulfill({ json: { ...answer, serverNow: new Date().toISOString() } });
  });
  await context.route("**/api/v1/events/*/queue/leave", (route) => route.fulfill({ status: 204 }));
  return state;
}

async function axe(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  return violations
    .filter((v) => v.impact === "serious" || v.impact === "critical")
    .map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
}

test("guarded: a signed-out visitor is sent to login and comes back to the waiting room", async ({ page }) => {
  await page.goto(`/events/${SLUG}/queue`);

  await expect(page).toHaveURL(`/login?returnUrl=${encodeURIComponent(`/events/${SLUG}/queue`)}`);
});

test("waits in line, then is admitted and taken to seat selection", async ({ context, page, baseURL }) => {
  const state = await arrange(context, page, baseURL!, () => [waiting(3), waiting(3), waiting(1), admitted()]);

  await page.goto(`/events/${SLUG}/queue`);

  await expect(page.getByRole("heading", { level: 1, name: "Sơn Tùng M-TP Live" })).toBeVisible();
  await expect(page.getByText("Còn 2 người phía trước")).toBeVisible();
  await expect(page).toHaveTitle("(#3) TicketRush");
  await expect(page.getByText("Đừng tải lại trang — bạn sẽ không mất lượt.")).toBeVisible();

  await expect(page.getByRole("heading", { name: "Đến lượt bạn!" })).toBeVisible({ timeout: 15_000 });
  await expect(page).toHaveURL(`/events/${SLUG}/seats`, { timeout: 10_000 });
  expect(state.joins).toBe(1);
  // The worker authenticates with the access token the page handed it.
  expect(state.authHeaders[0]).toBe("Bearer e2e-buyer-token");
});

test("polling runs in a Web Worker and keeps going while the tab is in the background", async ({ context, page, baseURL }) => {
  const state = await arrange(context, page, baseURL!, () => [waiting(50)]);
  await page.goto(`/events/${SLUG}/queue`);
  await expect(page.getByText("Còn 49 người phía trước")).toBeVisible();
  // The join answer renders before the worker has started.
  await expect.poll(() => page.workers().length).toBe(1);

  // Put another tab in front: the first one is now hidden.
  const other = await context.newPage();
  await other.goto("about:blank");
  await other.bringToFront();
  const before = state.polls;
  await expect.poll(() => state.polls, { timeout: 8_000 }).toBeGreaterThan(before + 2);
});

test("a presale phase asks for the code and keeps the buyer on the form until it is right", async ({ context, page, baseURL }) => {
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL! }]);
  await page.route("**/api/v1/auth/refresh", (route) => route.fulfill({ json: user }));
  const codes: unknown[] = [];
  await context.route("**/api/v1/events/*/queue/join", (route) => {
    const { accessCode } = route.request().postDataJSON() as { accessCode: string | null };
    codes.push(accessCode);
    return accessCode === "VIP-2026"
      ? route.fulfill({ json: waiting(7) })
      : route.fulfill({ status: 403, json: { status: 403, code: "queue.invalid_access_code" } });
  });
  await context.route("**/api/v1/events/*/queue/me", (route) => route.fulfill({ json: { ...waiting(7), serverNow: new Date().toISOString() } }));

  await page.goto(`/events/${SLUG}/queue`);
  await expect(page.getByRole("heading", { name: "Nhập mã truy cập" })).toBeVisible();
  await page.getByLabel("Mã truy cập").fill("sai");
  await page.getByRole("button", { name: "Vào phòng chờ" }).click();
  await expect(page.getByText("Mã truy cập không đúng.")).toBeVisible();

  await page.getByLabel("Mã truy cập").fill("VIP-2026");
  await page.getByRole("button", { name: "Vào phòng chờ" }).click();

  await expect(page.getByText("Còn 6 người phía trước")).toBeVisible();
  expect(codes).toEqual([null, "sai", "VIP-2026"]);
});

test("a stale access token is refreshed and the poll resumes", async ({ context, page, baseURL }) => {
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL! }]);
  let refreshes = 0;
  await page.route("**/api/v1/auth/refresh", (route) => {
    refreshes++;
    return route.fulfill({ json: { ...user, accessToken: refreshes === 1 ? "old-token" : "new-token" } });
  });
  await context.route("**/api/v1/events/*/queue/join", (route) => route.fulfill({ json: waiting(9) }));
  const tokens: string[] = [];
  await context.route("**/api/v1/events/*/queue/me", (route) => {
    const token = route.request().headers()["authorization"] ?? "";
    tokens.push(token);
    return token === "Bearer new-token"
      ? route.fulfill({ json: { ...waiting(8), serverNow: new Date().toISOString() } })
      : route.fulfill({ status: 401, json: { status: 401 } });
  });

  await page.goto(`/events/${SLUG}/queue`);

  await expect(page.getByText("Còn 7 người phía trước")).toBeVisible({ timeout: 15_000 });
  expect(tokens[0]).toBe("Bearer old-token");
});

test("leaving the waiting room returns to the event", async ({ context, page, baseURL }) => {
  await arrange(context, page, baseURL!, () => [waiting(20)]);
  await page.goto(`/events/${SLUG}/queue`);
  await expect(page.getByText("Còn 19 người phía trước")).toBeVisible();

  await page.getByRole("button", { name: "Rời phòng chờ" }).click();

  await expect(page).toHaveURL(`/events/${SLUG}`);
});

for (const colorScheme of ["light", "dark"] as const) {
  const states: [string, () => Body[], string][] = [
    ["waiting", () => [waiting(120)], "Còn 119 người phía trước"],
    [
      "before opening",
      () => [{ ...base(), status: "Pre", position: null, aheadOfYou: null, estimatedWaitSeconds: null, opensAt: new Date(Date.now() + 5 * MIN).toISOString(), pollAfterMs: 60_000 }],
      "Mọi người vào phòng chờ trước giờ mở bán sẽ được xếp thứ tự ngẫu nhiên.",
    ],
    ["sold out", () => [{ ...base(), status: "SoldOut", position: null, aheadOfYou: null, estimatedWaitSeconds: null }], "Vé đã bán hết"],
    ["closed", () => [{ ...base(), status: "Closed", position: null, aheadOfYou: null, estimatedWaitSeconds: null }], "Đợt bán đã kết thúc"],
  ];

  for (const [name, script, text] of states) {
    test(`${name} has no serious accessibility violations (${colorScheme})`, async ({ context, page, baseURL }) => {
      await arrange(context, page, baseURL!, script);
      await page.emulateMedia({ colorScheme });

      await page.goto(`/events/${SLUG}/queue`);
      await expect(page.getByText(text).first()).toBeVisible();

      expect(await axe(page)).toEqual([]);
    });
  }
}
