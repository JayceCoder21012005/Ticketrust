import AxeBuilder from "@axe-core/playwright";
import { devices, expect, test, type Page } from "@playwright/test";

/** Catalog pages against the mock api (e2e/mock-api.mjs): fixture sales are relative to its start-up. */

async function seriousViolations(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  return violations
    .filter((v) => v.impact === "serious" || v.impact === "critical")
    .map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
}

for (const colorScheme of ["light", "dark"] as const) {
  for (const path of ["/", "/events", "/events?category=sports&city=H%C3%A0+N%E1%BB%99i", "/events/son-tung-mtp-live"]) {
    test(`${path} has no serious accessibility violations (${colorScheme})`, async ({ page }) => {
      await page.emulateMedia({ colorScheme });
      await page.goto(path);
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
      await expect(page.getByRole("heading", { level: 2 }).first()).toBeVisible();

      expect(await seriousViolations(page)).toEqual([]);
    });
  }
}

test("home leads with the search hero, then the next sales to open, and links into the catalog", async ({ page }) => {
  await page.goto("/");

  const hero = page.getByRole("region", { name: "Săn vé nhanh, công bằng." });
  await expect(hero).toBeVisible();
  await expect(hero.getByRole("button", { name: /Tìm sự kiện, nghệ sĩ, địa điểm/ })).toBeVisible();
  const soon = page.getByRole("region", { name: "Sắp mở bán" });
  await expect(soon.getByRole("link", { name: /Vũ Cát Tường - Inner Space/ })).toBeVisible();
  await expect(soon.getByText("Mở bán sau").first()).toBeVisible();

  await page.getByRole("navigation", { name: "Khám phá theo danh mục" }).getByRole("link", { name: "Thể thao" }).click();

  await expect(page).toHaveURL("/events?category=sports");
  await expect(page.getByRole("heading", { level: 2, name: "V.League: Hà Nội FC vs Công An Hà Nội" })).toBeVisible();
  await expect(page.getByRole("heading", { level: 2, name: "Sơn Tùng M-TP Live" })).toHaveCount(0);
});

test("searching without accents finds the event, and the query lands in the URL", async ({ page }) => {
  await page.goto("/events");

  // The page is server-rendered: typing before React hydrates is lost, so type again until the debounced search fires.
  await expect(async () => {
    await page.getByRole("searchbox", { name: "Tìm kiếm" }).last().fill("");
    await page.getByRole("searchbox", { name: "Tìm kiếm" }).last().fill("son tung");
    await expect(page).toHaveURL("/events?q=son+tung", { timeout: 1500 });
  }).toPass({ timeout: 15_000 });
  await expect(page.getByRole("heading", { level: 2, name: "Sơn Tùng M-TP Live" })).toBeVisible();
  await expect(page.getByRole("heading", { level: 2, name: "Mỹ Tâm - Tri Kỷ" })).toHaveCount(0);
});

test("sidebar filters are shareable URL state and removable chips", async ({ page }) => {
  await page.goto("/events");

  await page.getByRole("complementary", { name: "Bộ lọc" }).getByText("Đà Nẵng", { exact: true }).click();

  await expect(page).toHaveURL("/events?city=%C4%90%C3%A0+N%E1%BA%B5ng");
  await expect(page.getByRole("heading", { level: 2, name: "Mỹ Tâm - Tri Kỷ" })).toBeVisible();
  await expect(page.getByRole("heading", { level: 2, name: "Sơn Tùng M-TP Live" })).toHaveCount(0);

  await page.getByRole("link", { name: "Bỏ lọc: Đà Nẵng" }).click();

  await expect(page).toHaveURL("/events");
  await expect(page.getByRole("heading", { level: 2, name: "Sơn Tùng M-TP Live" })).toBeVisible();
});

test("'Xem thêm' appends the next page instead of numbered pages", async ({ page }) => {
  await page.goto("/events");
  const cards = page.locator("main ul > li h2");
  await expect(cards).toHaveCount(9);

  await page.getByRole("button", { name: "Xem thêm" }).click();

  await expect(cards).toHaveCount(13);
  await expect(page.getByRole("button", { name: "Xem thêm" })).toHaveCount(0);
});

test("event page: buy goes through the waiting room, and the calendar file downloads", async ({ page }) => {
  await page.goto("/events/son-tung-mtp-live");

  await expect(page.getByRole("heading", { level: 1, name: "Sơn Tùng M-TP Live" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Mua vé" }).first()).toHaveAttribute("href", "/events/son-tung-mtp-live/queue");
  await expect(page.getByText("từ 800.000", { exact: false }).first()).toBeVisible();

  const download = page.waitForEvent("download");
  await page.getByRole("link", { name: "Thêm vào lịch" }).click();
  expect((await download).suggestedFilename()).toBe("son-tung-mtp-live.ics");
});

test("a queued sale opening within 30 minutes shows 'Vào phòng chờ'", async ({ page }) => {
  await page.goto("/events/vu-cat-tuong-inner-space");

  await expect(page.getByRole("link", { name: "Vào phòng chờ" }).first()).toHaveAttribute("href", "/events/vu-cat-tuong-inner-space/queue");
});

test("an ended event keeps its page but cannot be bought", async ({ page }) => {
  await page.goto("/events/jazz-night-saigon");

  await expect(page.getByRole("button", { name: "Sự kiện đã kết thúc" }).first()).toBeDisabled();
});

for (const slug of ["monsoon-festival-2026", "no-such-event", "Not_A_Slug"]) {
  test(`/events/${slug} shows the not-found page and is kept out of search engines`, async ({ page }) => {
    await page.goto(`/events/${slug}`);

    await expect(page.getByRole("heading", { name: "Không tìm thấy sự kiện" }).first()).toBeVisible();
    // The page shell streams before the event is looked up, so the status stays 200; Next marks it noindex.
    await expect(page.locator('head meta[name="robots"]').first()).toHaveAttribute("content", /noindex/);
  });
}

test("the Open Graph image is a PNG", async ({ request }) => {
  const html = await (await request.get("/events/son-tung-mtp-live")).text();
  const image = /<meta property="og:image" content="([^"]+)"/.exec(html)?.[1];
  expect(image).toBeTruthy();

  const response = await request.get(new URL(image!).pathname);

  expect(response.status()).toBe(200);
  expect(response.headers()["content-type"]).toBe("image/png");
});

test.describe("phone", () => {
  test.use({ viewport: devices["Pixel 7"].viewport, hasTouch: true, isMobile: true });

  for (const path of ["/", "/events", "/events/son-tung-mtp-live"]) {
    test(`${path} never scrolls sideways on a phone`, async ({ page }) => {
      await page.goto(path);
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();

      // On a phone the layout viewport grows to fit an overflowing page, so compare with the device width itself.
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width);
    });
  }

  test("the buy button sticks to the bottom of the event page", async ({ page }) => {
    await page.goto("/events/son-tung-mtp-live");

    const bar = page.getByRole("region", { name: "Mua vé" });
    await expect(bar).toBeVisible();
    await expect(bar.getByRole("link", { name: "Mua vé" })).toBeVisible();
  });

  test("filters open in a dialog and apply to the URL", async ({ page }) => {
    await page.goto("/events");

    await page.getByRole("button", { name: "Bộ lọc" }).click();
    const dialog = page.getByRole("dialog", { name: "Bộ lọc" });
    await expect(dialog).toBeVisible();
    await dialog.getByText("Hài kịch", { exact: true }).click();
    await expect(page).toHaveURL("/events?category=comedy");
    await dialog.getByRole("button", { name: "Xem kết quả" }).click();

    await expect(dialog).toBeHidden();
    await expect(page.getByRole("heading", { level: 2, name: "Saigon Tếu - Cười Xuyên Đêm" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Bộ lọc (1)" })).toBeVisible();
  });
});

test("card icons come from the cached sprite", async ({ page, request }) => {
  await page.goto("/events");
  await expect(page.locator('main use[href="/icons.svg#calendar-days"]').first()).toBeAttached();

  const sprite = await request.get("/icons.svg");
  expect(sprite.status()).toBe(200);
  expect(sprite.headers()["content-type"]).toContain("image/svg+xml");
});
