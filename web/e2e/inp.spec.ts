import { expect, test, type Page } from "@playwright/test";

/**
 * Spec 02 budget: INP < 200 ms on a mobile-class CPU. Lighthouse can't measure INP in the lab, so this
 * replays real interactions with 4× CPU throttling and reads the Event Timing API (what INP is built on).
 */
const INP_BUDGET_MS = 200;

async function throttleCpu(page: Page) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("Emulation.setCPUThrottlingRate", { rate: 4 });
}

async function observeInteractions(page: Page) {
  await page.evaluate(() => {
    const w = window as unknown as { __durations: number[] };
    w.__durations = [];
    new PerformanceObserver((list) => {
      // `interactionId` is in Chrome's Event Timing API but not yet in TypeScript's DOM lib.
      for (const entry of list.getEntries() as Array<PerformanceEventTiming & { interactionId?: number }>) {
        if (entry.interactionId) {
          w.__durations.push(entry.duration);
        }
      }
    }).observe({ type: "event", durationThreshold: 16, buffered: true } as PerformanceObserverInit);
  });
}

async function worstInteraction(page: Page): Promise<number> {
  // Event Timing entries are delivered after the next frame.
  await page.waitForTimeout(500);
  return page.evaluate(() => Math.max(0, ...(window as unknown as { __durations: number[] }).__durations));
}

test.describe("INP", () => {
  test.describe.configure({ mode: "serial" });

  test.beforeEach(async ({ page }) => {
    await throttleCpu(page);
  });

  test("home: toggling the theme responds within budget", async ({ page }) => {
    await page.goto("/", { waitUntil: "networkidle" });
    await observeInteractions(page);

    await page.getByRole("button", { name: "Đổi giao diện" }).click();
    await page.getByRole("button", { name: "Đổi giao diện" }).click();

    expect(await worstInteraction(page)).toBeLessThan(INP_BUDGET_MS);
  });

  test("design: shared component interactions respond within budget", async ({ page }) => {
    await page.goto("/design", { waitUntil: "networkidle" });
    await observeInteractions(page);

    await page.getByRole("button", { name: "Bắt đầu" }).click();
    await page.getByRole("button", { name: "Thử lại" }).click();
    await page.getByRole("button", { name: "Đổi giao diện" }).click();

    expect(await worstInteraction(page)).toBeLessThan(INP_BUDGET_MS);
  });
});
