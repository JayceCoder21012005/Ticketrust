import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

/**
 * UI refresh motion against the mock api: route changes run as view transitions (the poster morphs when the event page is
 * ready in the same commit, otherwise it enters), sections reveal on scroll, and reduced motion turns all of it off.
 */

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    const w = window as unknown as { __viewTransitions: number };
    w.__viewTransitions = 0;
    const original = document.startViewTransition?.bind(document);
    if (original) {
      document.startViewTransition = ((arg: Parameters<typeof original>[0]) => {
        w.__viewTransitions++;
        return original(arg);
      }) as typeof document.startViewTransition;
    }
  });
});

test("opening an event from a home card runs as a view transition", async ({ page }) => {
  await page.goto("/");
  const card = page.getByRole("region", { name: "Sắp mở bán" }).getByRole("link").first();
  const href = await card.getAttribute("href");

  await card.click();

  await expect(page).toHaveURL(href!);
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  expect(await page.evaluate(() => (window as unknown as { __viewTransitions: number }).__viewTransitions)).toBeGreaterThan(0);
});

test("sections reveal on scroll, and reduced motion shows them still", async ({ page }) => {
  await page.goto("/");
  const section = page.locator("section.reveal").first();
  expect(await section.evaluate((el) => getComputedStyle(el).animationName)).not.toBe("none");
  // The reveal only moves sections: text keeps its full contrast mid-scroll.
  expect(await section.evaluate((el) => getComputedStyle(el).opacity)).toBe("1");

  await page.emulateMedia({ reducedMotion: "reduce" });
  expect(await section.evaluate((el) => getComputedStyle(el).animationName)).toBe("none");
  expect(await section.evaluate((el) => getComputedStyle(el).opacity)).toBe("1");
});

test("with reduced motion, cards neither lift nor zoom", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.goto("/");
  const card = page.getByRole("region", { name: "Sắp mở bán" }).getByRole("link").first();

  await card.hover();

  expect(await card.evaluate((el) => getComputedStyle(el).transform)).toBe("none");
  expect(await card.locator("[data-poster] img, [data-poster] > div").first().evaluate((el) => getComputedStyle(el).transform)).toBe("none");
});

test("the home stays free of serious accessibility violations with motion on", async ({ page }) => {
  await page.goto("/");
  await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
  await page.waitForFunction(() => document.getAnimations().every((a) => a.playState !== "running" || a.effect?.getTiming().iterations === Infinity));

  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa"]).analyze();
  expect(violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
});
