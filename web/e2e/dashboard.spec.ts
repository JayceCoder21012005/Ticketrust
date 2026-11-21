import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signInAs } from "./organizer-fake";

/**
 * Spec 15 on the web against scripted endpoints (the .NET integration tests cover the real ones): the dashboard renders from the
 * first snapshot without waiting for the live feed, shows the KPIs, tiers, funnel, recent orders and the "reconnecting" state,
 * the chart loads its series, and "Xuất CSV" downloads the attendee file. Same `page.route` pattern as organizer.spec.ts.
 */
const snapshot = {
  eventId: "e1",
  at: new Date().toISOString(),
  sold: 1234,
  revenue: 987_000_000,
  currency: "VND",
  remaining: 766,
  tiers: [
    { tierId: "t1", name: "Ghế VIP", color: "#7c3aed", price: 1_500_000, capacity: 1000, sold: 600, held: 20, remaining: 380, revenue: 900_000_000 },
    { tierId: "t2", name: "Khu đứng", color: "#0f766e", price: 500_000, capacity: 1000, sold: 634, held: 0, remaining: 386, revenue: 87_000_000 },
  ],
  funnel: { queueJoined: 5000, admitted: 3000, held: 2000, ordersCreated: 1500, paid: 1234 },
  queue: { waiting: 2000, activeShoppers: 150, admissionRate: 5 },
  recentOrders: [{ orderId: "o1", buyerName: "Nguyễn An", tickets: 2, total: 3_000_000, paidAt: new Date().toISOString() }],
};

async function arrange(page: Page) {
  await page.route(/\/api\/v1\/organizer\/events\/e1\/dashboard$/, (route) => route.fulfill({ json: snapshot }));
  await page.route(/\/api\/v1\/organizer\/events\/e1\/sales-series(\?.*)?$/, (route) =>
    route.fulfill({
      json: {
        bucket: "1m",
        from: new Date(Date.now() - 7_200_000).toISOString(),
        to: new Date().toISOString(),
        points: [
          { at: new Date(Date.now() - 120_000).toISOString(), tierId: "t1", tickets: 12, revenue: 18_000_000 },
          { at: new Date(Date.now() - 60_000).toISOString(), tierId: "t2", tickets: 7, revenue: 3_500_000 },
        ],
      },
    }),
  );
  await page.route(/\/api\/v1\/organizer\/events\/e1\/export\.csv$/, (route) =>
    route.fulfill({ contentType: "text/csv; charset=utf-8", body: "﻿Mã đơn,Người mua\r\no1,Nguyễn An\r\n" }),
  );
  await page.route(/\/api\/v1\/events\/e1\/(seat-map|availability)$/, (route) =>
    route.fulfill({ json: route.request().url().endsWith("seat-map") ? { seats: [], sections: [], width: 0, height: 0 } : { serverNow: new Date().toISOString(), version: 0, unavailable: [], tiers: [] } }),
  );
  // No live feed in this test: the page must render from the snapshot and say it is reconnecting.
  await page.route(/\/hubs\/(organizer|events)\/negotiate.*$/, (route) => route.abort());
}

test("the dashboard renders from the snapshot, says it is reconnecting and exports the attendees", async ({ context, page, baseURL }) => {
  await signInAs(context, page, baseURL!);
  await arrange(page);

  await page.goto("/organizer/events/e1/dashboard");
  await expect(page.getByRole("heading", { level: 1, name: "Bảng điều khiển bán vé" })).toBeVisible();
  const kpis = page.getByRole("region", { name: "Chỉ số chính" });
  await expect(kpis.getByText("1.234")).toBeVisible();
  await expect(kpis.getByText("2.000")).toBeVisible();
  await expect(page.getByText("Đang kết nối lại…")).toBeVisible();
  await expect(page.getByRole("progressbar", { name: "Ghế VIP: đã bán 60%" })).toBeVisible();
  await expect(page.getByText("Nguyễn An")).toBeVisible();
  await expect(page.locator("[data-slot=chart]")).toBeVisible();

  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  expect(violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);

  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Xuất CSV" }).click();
  expect((await download).suggestedFilename()).toBe("attendees-e1.csv");
});

test("another organizer's event says it was not found", async ({ context, page, baseURL }) => {
  await signInAs(context, page, baseURL!);
  await page.route(/\/api\/v1\/organizer\/events\/e2\/dashboard$/, (route) =>
    route.fulfill({ status: 404, contentType: "application/problem+json", body: JSON.stringify({ status: 404, code: "dashboard.not_found" }) }),
  );
  await page.route(/\/hubs\/organizer\/negotiate.*$/, (route) => route.abort());

  await page.goto("/organizer/events/e2/dashboard");
  await expect(page.getByText("Không tìm thấy sự kiện")).toBeVisible();
});
