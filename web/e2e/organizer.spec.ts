import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { draftEvent, OrganizerFake, signInAs } from "./organizer-fake";

const DAY = 86_400_000;
/** Vietnam wall clock of `ms` for the date and time inputs. */
const vn = (ms: number) => {
  const d = new Date(ms + 7 * 3_600_000).toISOString();
  return { date: d.slice(0, 10), time: d.slice(11, 16) };
};
// A 1×1 PNG: enough to go through pick → crop → upload.
const PNG = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==", "base64");

const tiers = [
  { id: "t1", name: "Ghế VIP", price: 1_500_000, kind: "Seated" as const, capacity: null, color: "#7c3aed" },
  { id: "t2", name: "Khu đứng", price: 500_000, kind: "GeneralAdmission" as const, capacity: 300, color: "#0f766e" },
];

async function axe(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  return violations
    .filter((v) => v.impact === "serious" || v.impact === "critical")
    .map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
}

test.describe("organizer area", () => {
  test.beforeEach(async ({ context, page, baseURL }) => {
    await signInAs(context, page, baseURL!);
  });

  for (const colorScheme of ["light", "dark"] as const) {
    for (const path of ["/organizer/events", "/organizer/venues", ...["info", "tiers", "layout", "phases", "review"].map((s) => `/organizer/events/e1/${s}`)]) {
      test(`${path} has no serious accessibility violations (${colorScheme})`, async ({ page }) => {
        const event = draftEvent({
          tiers,
          layout: { sections: [{ code: "A", tierId: "t1", kind: "seated", rows: 4, seatsPerRow: 10, origin: { x: 300, y: 120 }, curve: 0.1 }] },
          phases: [{ id: "p1", name: "Mở bán", opensAt: new Date(Date.now() + DAY).toISOString(), closesAt: new Date(Date.now() + 10 * DAY).toISOString(), queueEnabled: true, maxTicketsPerUser: 4, hasAccessCode: true }],
        });
        await new OrganizerFake(event).install(page);
        await page.emulateMedia({ colorScheme });

        await page.goto(path);
        // Guards against passing on the login page if the session restore ever regresses.
        await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
        await expect(page).toHaveURL(path);
        await expect(page.getByRole("navigation", { name: "Đường dẫn" })).toBeVisible();

        expect(await axe(page)).toEqual([]);
      });
    }
  }

  test("a draft goes from quick create to published", async ({ page }) => {
    test.setTimeout(90_000);
    const fake = new OrganizerFake();
    await fake.install(page);
    const start = vn(Date.now() + 30 * DAY);
    const opens = vn(Date.now() + 2 * DAY);
    const closes = vn(Date.now() + 20 * DAY);

    await page.goto("/organizer/events");
    await page.getByRole("button", { name: "Tạo sự kiện" }).click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel("Tên sự kiện").fill("Đêm nhạc mùa đông");
    await dialog.getByLabel("Danh mục").selectOption("concert");
    await dialog.getByLabel("Địa điểm").selectOption("v1");
    await dialog.getByLabel("Ngày bắt đầu").fill(start.date);
    await dialog.getByLabel("Giờ bắt đầu").fill("19:30");
    await dialog.getByLabel("Ngày kết thúc").fill(start.date);
    await dialog.getByLabel("Giờ kết thúc").fill("22:30");
    await dialog.getByRole("button", { name: "Tạo bản nháp" }).click();
    await expect(page).toHaveURL(/\/organizer\/events\/new-1\/info$/);

    // 1. Cover
    await page.locator("#cover-input").setInputFiles({ name: "cover.png", mimeType: "image/png", buffer: PNG });
    await page.getByRole("button", { name: "Dùng ảnh này" }).click();
    await expect(page.getByRole("status")).toContainText("Đã lưu lúc");

    // 2. Tiers
    await page.getByRole("link", { name: /Hạng vé/ }).click();
    await page.getByRole("button", { name: "Thêm hạng vé" }).click();
    await page.getByLabel("Tên hạng mới 1").fill("Ghế thường");
    await page.getByLabel("Giá hạng Ghế thường").fill("800000");
    await expect(page.getByRole("status")).toContainText("Đã lưu lúc");

    // 3. Layout from a preset
    await page.getByRole("link", { name: /Sơ đồ/ }).click();
    await page.getByLabel("Bắt đầu từ mẫu").selectOption("theater-3");
    await page.getByRole("button", { name: "Dùng mẫu" }).click();
    await expect(page.getByRole("img", { name: /Xem trước sơ đồ: 328 ghế/ })).toBeVisible();
    await expect(page.getByRole("status")).toContainText("Đã lưu lúc");

    // 4. Phases
    await page.getByRole("link", { name: /Mở bán/ }).click();
    await page.getByRole("button", { name: "Thêm đợt mở bán" }).click();
    const phase = page.getByRole("group", { name: /^Đợt 1/ });
    await phase.getByLabel("Ngày mở").fill(opens.date);
    await phase.getByLabel("Giờ mở").fill(opens.time);
    await phase.getByLabel("Ngày đóng").fill(closes.date);
    await phase.getByLabel("Giờ đóng").fill(closes.time);
    await expect(page.getByRole("status")).toContainText("Đã lưu lúc");

    // 5. Review and publish
    await page.getByRole("link", { name: /Kiểm tra & Xuất bản/ }).click();
    await expect(page.getByRole("link", { name: /^Sửa:/ })).toHaveCount(0);
    await page.getByRole("button", { name: "Xuất bản", exact: true }).click();
    await page.getByRole("button", { name: "Xuất bản ngay" }).click();

    await expect(page.getByText("Đã xuất bản", { exact: true }).first()).toBeVisible();
    expect(fake.events.get("new-1")!.status).toBe("Published");
  });

  // @perf: wall-clock budgets depend on the machine. Baseline (5 runs each, same machine, 2026-11-04, one fresh
  // `next start` per run), first redraw of the 20x30 section:
  //   main               43.9 / 187.6 / 253.2 / 327.2 / 1332.6 ms   (median 253.2)
  //   fix/07-followups   232.3 / 232.8 / 257.5 / 300.5 / 952.0 ms   (median 257.5)
  // so the branch is no slower than main, and the 100 ms of the original assertion was never met on this machine:
  // that first redraw is dominated by one-off work (JIT, first render of 600 circles) with a heavy tail.
  // The test therefore measures two things:
  //   warm redraw  (2 -> 20 rows after the section has rendered once): the real cost of the preview, ~21-30 ms
  //                measured; always asserted against the spec's 100 ms.
  //   first redraw (1 -> 20 rows, cold): asserted against 100 ms only when PERF_BUDGET=strict is set, for a stable,
  //                otherwise idle runner (CI perf job: `PERF_BUDGET=strict npx playwright test --grep @perf`).
  //                Elsewhere it gets 1500 ms: ~6x the median measured on main (3x would flake on the tail), enough
  //                to catch a real regression (O(n^2) redraw, a blocked main thread) without failing on a busy laptop.
  // Both timings are attached to the test as `perf` annotations.
  const STRICT = process.env.PERF_BUDGET === "strict";
  const BUDGET_MS = { warm: 100, cold: STRICT ? 100 : 1500 };

  test("the layout preview draws a 20×30 section within the perf budget in the browser", { tag: "@perf" }, async ({ page }) => {
    await new OrganizerFake(
      draftEvent({ tiers, layout: { sections: [{ code: "A", tierId: "t1", kind: "seated", rows: 1, seatsPerRow: 30, origin: { x: 100, y: 50 }, curve: 0.15 }] } }),
    ).install(page);
    await page.goto("/organizer/events/e1/layout");
    const seats = page.getByRole("img", { name: /^Xem trước sơ đồ/ }).locator("circle");
    await expect(seats).toHaveCount(30);

    // Changes the rows field and times it until two animation frames later (the preview has painted).
    const setRows = (rows: string) =>
      page.evaluate(async (value) => {
        const input = document.getElementById("section-A-rows") as HTMLInputElement;
        const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!.set!;
        const started = performance.now();
        setValue.call(input, value);
        input.dispatchEvent(new Event("input", { bubbles: true }));
        await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        return { ms: performance.now() - started, seats: document.querySelectorAll('svg[aria-label^="Xem trước sơ đồ"] circle').length };
      }, rows);

    const cold = await setRows("20");
    expect(cold.seats).toBe(600);
    expect((await setRows("2")).seats).toBe(60);
    const warm = await setRows("20");
    expect(warm.seats).toBe(600);

    test.info().annotations.push(
      { type: "perf", description: `first redraw ${cold.ms.toFixed(1)} ms (budget ${BUDGET_MS.cold} ms)` },
      { type: "perf", description: `warm redraw ${warm.ms.toFixed(1)} ms (budget ${BUDGET_MS.warm} ms)` },
    );
    expect(warm.ms, "warm redraw").toBeLessThan(BUDGET_MS.warm);
    expect(cold.ms, "first redraw").toBeLessThan(BUDGET_MS.cold);
  });

  test("published inventory is read-only behind the lock banner", async ({ page }) => {
    await new OrganizerFake(draftEvent({ status: "Published", tiers })).install(page);

    await page.goto("/organizer/events/e1/tiers");

    await expect(page.getByText(/Đã khoá sau khi xuất bản/)).toBeVisible();
    await expect(page.getByLabel("Tên hạng Ghế VIP")).toBeDisabled();
  });

  test("cancelling requires typing the title", async ({ page }) => {
    const fake = new OrganizerFake(draftEvent({ status: "Published", tiers }));
    await fake.install(page);
    await page.goto("/organizer/events/e1/review");

    await page.getByRole("button", { name: "Huỷ sự kiện" }).click();
    const confirm = page.getByRole("button", { name: "Xác nhận huỷ" });
    await page.getByLabel("Gõ tên sự kiện để xác nhận").fill("Đêm nhạc");
    await expect(confirm).toBeDisabled();
    await page.getByLabel("Gõ tên sự kiện để xác nhận").fill("Đêm nhạc thử");
    await confirm.click();

    await expect.poll(() => fake.events.get("e1")!.status).toBe("Cancelled");
  });
});

test("a customer sees the organizer-account message", async ({ context, page, baseURL }) => {
  await signInAs(context, page, baseURL!, ["Customer"]);

  await page.goto("/organizer/events");

  await expect(page.getByRole("heading", { name: "Bạn cần tài khoản nhà tổ chức" })).toBeVisible();
});
