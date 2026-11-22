import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

// Spec 17: /lab is a public, static page built from the committed results.json; it must be accessible in both themes.
for (const scheme of ["light", "dark"] as const) {
  test(`the load lab renders its scenarios and has no serious accessibility violations (${scheme})`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await page.goto("/lab");

    await expect(page.getByRole("heading", { level: 1, name: "Phòng thí nghiệm tải" })).toBeVisible();
    await expect(page.getByRole("table", { name: "Kịch bản" })).toBeVisible();
    await expect(page.getByRole("cell", { name: /01 · Sale-open spike/ })).toBeVisible();

    const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
    expect(violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
}
