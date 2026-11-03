import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/** The api is not running in e2e: these tests mock it at the network layer, like the browser would see it. */
const loginResponse = {
  accessToken: "e2e-secret-access-token",
  expiresAt: new Date(Date.now() + 600_000).toISOString(),
  user: { id: "1", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

for (const colorScheme of ["light", "dark"] as const) {
  for (const path of ["/login", "/register", "/forgot-password", "/reset-password?email=an%40example.com&token=t"]) {
    test(`${path} has no serious accessibility violations (${colorScheme})`, async ({ page }) => {
      await page.emulateMedia({ colorScheme });
      await page.goto(path);
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();

      const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
      const serious = violations.filter((v) => v.impact === "serious" || v.impact === "critical");

      expect(serious.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`)).toEqual([]);
    });
  }
}

test("guarded pages send signed-out visitors to login with a returnUrl", async ({ page }) => {
  await page.goto("/account?tab=profile");

  await expect(page).toHaveURL("/login?returnUrl=%2Faccount%3Ftab%3Dprofile");
});

test("a stale session hint still ends at login once the refresh is rejected", async ({ page, context, baseURL }) => {
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL! }]);
  await page.route("**/api/v1/auth/refresh", (route) =>
    route.fulfill({ status: 401, json: { code: "auth.refresh_invalid", status: 401 } }),
  );

  await page.goto("/account");

  await expect(page).toHaveURL("/login?returnUrl=%2Faccount");
});

async function signIn(page: Page, returnUrl: string) {
  await page.route("**/api/v1/auth/login", (route) => route.fulfill({ json: loginResponse }));
  await page.goto(`/login?returnUrl=${encodeURIComponent(returnUrl)}`);
  await page.getByLabel("Email").fill("an@example.com");
  await page.getByLabel("Mật khẩu", { exact: true }).fill("matkhau123");
  await page.getByLabel("Mật khẩu", { exact: true }).press("Enter");
}

test("signing in returns to the returnUrl and keeps the access token out of web storage", async ({ page }) => {
  await signIn(page, "/design");

  await expect(page).toHaveURL("/design");
  const stored = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
  expect(stored).not.toContain(loginResponse.accessToken);
});

test("an off-site returnUrl is ignored", async ({ page }) => {
  await signIn(page, "https://evil.com");

  await expect(page).toHaveURL("/");
});

test("wrong credentials show the friendly message", async ({ page }) => {
  await page.route("**/api/v1/auth/login", (route) =>
    route.fulfill({ status: 401, json: { code: "auth.invalid_credentials", status: 401 } }),
  );
  await page.goto("/login");

  await page.getByLabel("Email").fill("an@example.com");
  await page.getByLabel("Mật khẩu", { exact: true }).fill("wrongpass1");
  await page.getByRole("button", { name: "Đăng nhập" }).click();

  // Scoped to the form: Next.js also renders an (empty) role="alert" route announcer.
  await expect(page.locator("form").getByRole("alert")).toHaveText("Email hoặc mật khẩu không đúng.");
});
