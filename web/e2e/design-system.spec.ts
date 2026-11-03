import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

for (const colorScheme of ["light", "dark"] as const) {
  for (const path of ["/design", "/"]) {
    test(`${path} has no serious accessibility violations (${colorScheme})`, async ({ page }) => {
      await page.emulateMedia({ colorScheme });
      await page.goto(path);

      const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
      const serious = violations.filter((v) => v.impact === "serious" || v.impact === "critical");

      expect(serious.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`)).toEqual([]);
    });
  }
}

for (const path of ["/design", "/"]) {
  test(`${path} runs without uncaught client errors`, async ({ page }) => {
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));

    await page.goto(path, { waitUntil: "networkidle" });

    expect(errors).toEqual([]);
    await expect(page.locator("html")).toHaveAttribute("lang", "vi");
  });
}

test("Vietnamese text renders in Be Vietnam Pro in every weight", async ({ page }) => {
  await page.goto("/design");
  await page.evaluate(() => document.fonts.ready);

  const sample = "Sơn Tùng ắ ằ ẳ ẵ ặ ơ ư đ Đ";
  for (const weight of [400, 500, 600, 700]) {
    const result = await page.evaluate(
      async ({ weight, sample }) => {
        const family = getComputedStyle(document.body).fontFamily.split(",")[0].trim();
        await document.fonts.load(`${weight} 16px ${family}`, sample);
        const faces = [...document.fonts].filter(
          (f) => family.includes(f.family.replace(/["']/g, "")) && f.weight === String(weight) && f.status === "loaded",
        );
        // The Vietnamese subset covers U+1EA0–1EF9 (ắ, ặ, ơ…); it must be one of the loaded faces.
        return { family, loaded: faces.length, vietnamese: faces.some((f) => /1EA0|1EA0-1EF9/i.test(f.unicodeRange)) };
      },
      { weight, sample },
    );

    expect(result.family, "body font").toMatch(/Be_Vietnam_Pro|Be Vietnam Pro/);
    expect(result.vietnamese, `weight ${weight} Vietnamese subset loaded`).toBe(true);
  }
});
