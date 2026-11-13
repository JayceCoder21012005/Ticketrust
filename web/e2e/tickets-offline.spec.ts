import AxeBuilder from "@axe-core/playwright";
import { expect, test, type BrowserContext, type Page, type Route } from "@playwright/test";
import { base64ToBytes, qrPayload } from "../src/lib/tickets/qr-code";
import { encodeQr } from "../src/lib/tickets/qr-matrix";
import { qrPath } from "../src/lib/tickets/qr-svg";
import { formatClock } from "../src/lib/format";

/**
 * The offline ticket wallet (spec 10, "With the network off, a previously opened ticket still shows a valid rotating
 * QR"), against the production build: the service worker (`public/sw.js`) keeps the page shell and its static assets,
 * IndexedDB (`ticketrush-tickets`) keeps the ticket and its `qr-key` secret. The api is scripted with `context.route`
 * like tickets.spec.ts; "offline" is the browser's own (`context.setOffline(true)`), and the scripted api also refuses
 * to answer while offline, so no route can hand the page an answer the network would not.
 */
const MIN = 60_000;
const SKEW = 2 * MIN;

const account = (id: string, fullName: string) => ({
  accessToken: `e2e-${id}-token`,
  expiresAt: new Date(Date.now() + 10 * MIN).toISOString(),
  user: { id, email: `${id}@example.com`, fullName, phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
});
const userA = account("user-a", "Nguyễn An");
const userB = account("user-b", "Trần Bình");

const ticketId = (n: number) => `0000000${n}-aaaa-bbbb-cccc-eeeeeeeeeeee`;
const ID = ticketId(2);
const SECRET = Buffer.from(Array.from({ length: 32 }, (_, i) => 255 - i * 5)).toString("base64");

type Detail = ReturnType<typeof detail>;
const detail = (id: string, event: Record<string, unknown> = {}) => ({
  id,
  orderId: "o1",
  tierName: "Hạng VIP",
  seat: { section: "B", row: "4", number: "17" },
  status: "Valid",
  holderName: "Nguyễn An",
  orderTicketIds: [ticketId(1), ID, ticketId(3)],
  event: {
    eventId: "e2",
    slug: "dem-nhac-ngoai-tuyen",
    title: "Đêm nhạc ngoại tuyến",
    coverImageUrl: null,
    startsAt: "2099-12-20T13:00:00Z",
    endsAt: "2099-12-20T16:00:00Z",
    venueName: "Nhà hát Hòa Bình",
    city: "TP.HCM",
    status: "Published",
    ...event,
  },
});

type Api = {
  user: ReturnType<typeof account>;
  offline: boolean;
  details: Record<string, Detail>;
  /** What `qr-key` answers: the key, or an error status with its problem code. */
  key: "ok" | 404 | 409;
  keyCalls: string[];
  /** How `auth/refresh` answers: the current user, or no answer at all (a network failure on that one call). */
  refresh: "user" | "unreachable";
};

const problem = { 404: "ticket.not_found", 409: "ticket.not_displayable" } as const;

/** An api clock `SKEW` ahead of the page's (fake) clock. */
const serverNow = async (page: Page) => new Date((await page.evaluate(() => Date.now())) + SKEW).toISOString();

/** Signs `api.user` in and scripts the endpoints the buyer pages call. While `api.offline`, every call fails like a dead network. */
async function arrange(context: BrowserContext, page: Page, baseURL: string, details: Record<string, Detail> = { [ID]: detail(ID) }): Promise<Api> {
  const api: Api = { user: userA, offline: false, details, key: "ok", keyCalls: [], refresh: "user" };
  await context.addCookies([{ name: "tr_session", value: "1", url: baseURL }]);
  const scripted = async (pattern: RegExp, answer: (url: URL) => Promise<Parameters<Route["fulfill"]>[0]>) =>
    context.route(pattern, async (route) => (api.offline ? route.abort("internetdisconnected") : route.fulfill(await answer(new URL(route.request().url())))));

  await context.route(/\/api\/v1\/auth\/refresh$/, (route) =>
    api.offline || api.refresh === "unreachable" ? route.abort("internetdisconnected") : route.fulfill({ json: api.user }),
  );
  await scripted(/\/api\/v1\/auth\/login$/, async () => ({ json: api.user }));
  await scripted(/\/api\/v1\/auth\/logout$/, async () => ({ status: 204, headers: { "set-cookie": "tr_session=; Path=/; Max-Age=0" } }));
  await scripted(/\/api\/v1\/me$/, async () => ({ json: api.user.user }));
  await scripted(/\/api\/v1\/me\/notifications(\?.*)?$/, async () => ({ json: { items: [], nextCursor: null, unreadCount: 0 } }));
  await scripted(/\/api\/v1\/me\/tickets(\?.*)?$/, async () => ({ json: { events: [] } }));
  await scripted(/\/api\/v1\/time$/, async () => ({ json: { serverNow: await serverNow(page) } }));
  await scripted(/\/api\/v1\/tickets\/[^/?]+$/, async (url) => {
    const found = api.user === userA ? api.details[url.pathname.split("/").at(-1)!] : undefined;
    return found ? { json: found } : { status: 404, json: { code: "ticket.not_found", status: 404 } };
  });
  await scripted(/\/api\/v1\/tickets\/[^/?]+\/qr-key$/, async (url) => {
    const id = url.pathname.split("/").at(-2)!;
    api.keyCalls.push(id);
    if (api.key !== "ok") return { status: api.key, json: { code: problem[api.key], status: api.key } };
    return { json: { ticketId: id, secret: SECRET, stepSeconds: 15, format: "TR1" }, headers: { "cache-control": "no-store" } };
  });
  // The live connection (notifications) is not what these tests are about.
  await scripted(/\/hubs\//, async () => ({ status: 404, body: "" }));
  return api;
}

async function goOffline(context: BrowserContext, api: Api) {
  api.offline = true;
  await context.setOffline(true);
}

async function goOnline(context: BrowserContext, api: Api) {
  api.offline = false;
  await context.setOffline(false);
}

const qrImage = (page: Page) => page.getByRole("img", { name: "Mã QR vào cửa, đổi mỗi 15 giây" });
const offlineHint = (page: Page) => page.getByText("Đang dùng mã ngoại tuyến");

async function expectedPath(id: string, unixSeconds: number) {
  return qrPath(encodeQr(await qrPayload(id, base64ToBytes(SECRET), unixSeconds)));
}

/** Freezes the page clock 5 s into the next 15-second step and returns that step's start (device time). */
async function freezeFiveSecondsIntoAStep(page: Page): Promise<number> {
  const now = await page.evaluate(() => Date.now());
  const step = Math.ceil((now + 1_000) / 15_000) * 15_000;
  await page.clock.pauseAt(step + 4_500);
  await page.clock.runFor(1_000);
  return step;
}

/** What the offline stores hold, read without ever creating the database (that would skip the app's upgrade). */
async function offlineState(page: Page) {
  return page.evaluate(async () => {
    const names = (await indexedDB.databases()).map((db) => db.name);
    let keys: string[] = [];
    let tickets: string[] = [];
    if (names.includes("ticketrush-tickets")) {
      [keys, tickets] = await new Promise<[string[], string[]]>((resolve, reject) => {
        const request = indexedDB.open("ticketrush-tickets");
        request.onerror = () => reject(request.error);
        request.onsuccess = () => {
          const db = request.result;
          if (!db.objectStoreNames.contains("keys")) {
            db.close();
            resolve([[], []]);
            return;
          }
          const transaction = db.transaction(["keys", "tickets"]);
          const k = transaction.objectStore("keys").getAllKeys();
          const t = transaction.objectStore("tickets").getAllKeys();
          transaction.oncomplete = () => {
            db.close();
            resolve([k.result.map(String), t.result.map(String)]);
          };
        };
      });
    }
    const caches_ = (await caches.keys()).filter((name) => name.startsWith("ticketrush-wallet-"));
    const cachedUrls: string[] = [];
    const bodies: string[] = [];
    for (const name of caches_) {
      const cache = await caches.open(name);
      for (const request of await cache.keys()) {
        cachedUrls.push(new URL(request.url).pathname);
        const response = await cache.match(request);
        if (response && (response.headers.get("content-type") ?? "").startsWith("text/html")) bodies.push(await response.text());
      }
    }
    const workers = (await navigator.serviceWorker.getRegistrations()).length;
    return { keys, tickets, caches: caches_, cachedUrls, bodies, workers };
  });
}

/** Opens the wallet online and waits until the worker controls the page and everything is stored for offline use. */
async function openOnlineAndStore(page: Page, id = ID) {
  await page.goto(`/tickets/${id}`);
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  await page.waitForFunction(() => navigator.serviceWorker.controller !== null);
  await expect
    .poll(async () => {
      const state = await offlineState(page);
      return {
        key: state.keys.includes(id),
        ticket: state.tickets.includes(id),
        page: state.cachedUrls.includes(`/tickets/${id}`),
        missingAssets: await uncachedAssets(page),
      };
    })
    .toEqual({ key: true, ticket: true, page: true, missingAssets: [] });
}

/** The page's own static files (scripts, styles, fonts) that the worker has not cached yet. */
const uncachedAssets = (page: Page) =>
  page.evaluate(async () => {
    const urls = [
      ...performance.getEntriesByType("resource").map((entry) => entry.name),
      ...[...document.querySelectorAll<HTMLScriptElement>("script[src]")].map((script) => script.src),
    ]
      .map((raw) => new URL(raw, location.href))
      .filter((url) => url.origin === location.origin && url.pathname.startsWith("/_next/static/"));
    const missing: string[] = [];
    for (const url of new Set(urls.map((u) => u.href))) {
      if (!(await caches.match(url, { ignoreVary: true }))) missing.push(new URL(url).pathname);
    }
    return missing;
  });

test.describe("offline ticket wallet", () => {
  test("the sw.js headers let it control the site and revalidate on every update", async ({ request }) => {
    const response = await request.get("/sw.js");
    expect(response.status()).toBe(200);
    expect(response.headers()["content-type"]).toMatch(/^application\/javascript/);
    expect(response.headers()["cache-control"]).toBe("no-cache");
    expect(response.headers()["service-worker-allowed"]).toBe("/");
  });

  test("a ticket opened online keeps showing a valid rotating QR with the network off", async ({ context, page, baseURL }) => {
    await page.clock.install({ time: Date.UTC(2026, 10, 12, 12, 0, 0) });
    const api = await arrange(context, page, baseURL!);

    // (i) Online: the worker is registered, the page shell and the ticket and its key are stored.
    await openOnlineAndStore(page);
    const stored = await offlineState(page);
    expect(stored.workers).toBe(1);
    expect(stored.cachedUrls.some((url) => url.startsWith("/_next/static/"))).toBe(true);
    // Only the shell and static files: never the api, and the cached page holds neither the secret nor the holder.
    expect(stored.cachedUrls.filter((url) => !url.startsWith("/_next/static/"))).toEqual([`/tickets/${ID}`]);
    expect(stored.bodies.join("")).not.toContain(SECRET);
    expect(stored.bodies.join("")).not.toContain("Nguyễn An");
    expect(api.keyCalls).toEqual([ID]);

    // (ii) Offline: the reload is answered by the worker, the network really is gone (for the worker too).
    await goOffline(context, api);
    const reload = await page.reload();
    expect(reload?.fromServiceWorker()).toBe(true);
    const worker = context.serviceWorkers()[0]!;
    expect(await worker.evaluate(() => fetch("/favicon.ico", { cache: "no-store" }).then(() => "online", () => "offline"))).toBe("offline");

    await expect(page.getByRole("heading", { level: 1, name: "Đêm nhạc ngoại tuyến" })).toBeFocused();
    const seat = page.getByRole("region", { name: "Thông tin chỗ ngồi" });
    await expect(seat).toContainText("Nguyễn An");
    await expect(seat).toContainText("17");
    await expect(qrImage(page)).toBeVisible();
    await expect(offlineHint(page)).toBeVisible();

    // The code rotates on the server clock saved online (2 minutes ahead), across a 15-second boundary.
    const step = await freezeFiveSecondsIntoAStep(page);
    const path = qrImage(page).locator("path[data-modules]");
    const first = await expectedPath(ID, (step + SKEW + 5_000) / 1000);
    await expect(path).toHaveAttribute("d", first);
    await expect(page.getByRole("timer")).toHaveText(formatClock(step + SKEW + 5_000));
    await page.clock.runFor(15_000);
    const next = await expectedPath(ID, (step + SKEW + 20_000) / 1000);
    expect(next).not.toBe(first);
    await expect(path).toHaveAttribute("d", next);
    expect(await page.content()).not.toContain(SECRET);

    // Back online, the background retry refreshes the key and the hint goes away.
    await goOnline(context, api);
    await page.clock.runFor(60_000);
    await expect(offlineHint(page)).toHaveCount(0);
    expect(api.keyCalls.length).toBeGreaterThan(1);
  });

  test("logout clears the keys, the ticket cache and the worker; the next user sees none of it", async ({ context, page, baseURL }) => {
    const api = await arrange(context, page, baseURL!);
    await openOnlineAndStore(page);

    // (iii) User A signs out from the account page.
    await page.goto("/account");
    await page.getByRole("button", { name: "Đăng xuất" }).click();
    await expect(page).toHaveURL(/\/$/);
    await expect.poll(async () => {
      const state = await offlineState(page);
      return { keys: state.keys, tickets: state.tickets, caches: state.caches, workers: state.workers };
    }).toEqual({ keys: [], tickets: [], caches: [], workers: 0 });

    // User B signs in on the same browser profile.
    api.user = userB;
    await context.addCookies([{ name: "tr_session", value: "1", url: baseURL! }]);
    await page.goto("/account/tickets");
    await expect(page.getByText("Bạn chưa có vé nào sắp diễn ra")).toBeVisible();
    const state = await offlineState(page);
    expect(state.keys).toEqual([]);
    expect(state.tickets).toEqual([]);

    // Offline, A's ticket is not there to open: no page from the worker, no QR, nothing of A.
    await goOffline(context, api);
    await page.goto(`/tickets/${ID}`).catch(() => null);
    await expect(qrImage(page)).toHaveCount(0);
    expect(await page.content()).not.toContain("Nguyễn An");
    expect(await page.content()).not.toContain(SECRET);
  });

  test("another user signing in on the login page never gets the previous user's offline ticket", async ({ context, page, baseURL }) => {
    const api = await arrange(context, page, baseURL!);
    await openOnlineAndStore(page);

    // A's session ends without a logout here (the tab is simply left); B signs in on the login page of this browser.
    api.user = userB;
    api.refresh = "unreachable";
    await page.goto(`/login?returnUrl=${encodeURIComponent("/account/tickets")}`);
    await page.getByLabel("Email").fill("user-b@example.com");
    await page.getByLabel("Mật khẩu", { exact: true }).fill("matkhau123");
    await page.getByLabel("Mật khẩu", { exact: true }).press("Enter");
    await expect(page).toHaveURL(/\/account\/tickets$/);
    api.refresh = "user";
    await expect.poll(async () => {
      const state = await offlineState(page);
      return { keys: state.keys, tickets: state.tickets, shell: state.cachedUrls.filter((url) => url.startsWith("/tickets/")) };
    }).toEqual({ keys: [], tickets: [], shell: [] });

    // B, signed in in this tab, goes offline and opens A's ticket from the history: nothing of A.
    await goOffline(context, api);
    await page.goto(`/tickets/${ID}`).catch(() => null);
    await expect(qrImage(page)).toHaveCount(0);
    expect(await page.content()).not.toContain("Nguyễn An");

    // And after a reload (no session offline): the same.
    await page.reload().catch(() => null);
    await expect(qrImage(page)).toHaveCount(0);
    expect(await page.content()).not.toContain("Nguyễn An");
  });

  for (const status of [404, 409] as const) {
    test(`a ${status} from qr-key while online deletes what was cached for the ticket`, async ({ context, page, baseURL }) => {
      const api = await arrange(context, page, baseURL!);
      await openOnlineAndStore(page);

      // (iv) The ticket changed elsewhere (listed, voided, checked in: 409; gone: 404).
      api.key = status;
      await page.reload();
      await expect(page.getByRole("heading", { name: status === 404 ? "Không tìm thấy vé" : "Không thể hiển thị mã QR" })).toBeVisible();
      await expect.poll(async () => {
        const state = await offlineState(page);
        return { keys: state.keys, tickets: state.tickets };
      }).toEqual({ keys: [], tickets: [] });

      // Offline now, nothing of the old answer is used: the ticket is simply not available offline any more.
      await goOffline(context, api);
      await page.reload();
      await expect(page.getByRole("heading", { level: 2, name: "Chưa xem được vé này khi không có mạng" })).toBeVisible();
      await expect(qrImage(page)).toHaveCount(0);
    });
  }

  test("a ticket whose event has ended shows the ended state offline, and is purged", async ({ context, page, baseURL }) => {
    const start = Date.UTC(2026, 10, 12, 12, 0, 0);
    await page.clock.install({ time: start });
    // On the server clock (2 minutes ahead) the event ends 5 minutes from now.
    const endsAt = new Date(start + SKEW + 5 * MIN).toISOString();
    const api = await arrange(context, page, baseURL!, { [ID]: detail(ID, { startsAt: new Date(start - 60 * MIN).toISOString(), endsAt }) });
    await openOnlineAndStore(page);
    await expect(qrImage(page)).toBeVisible();

    // (v) Offline, the event ends while the screen is open: the code goes away.
    await goOffline(context, api);
    await page.clock.runFor(6 * MIN);
    await expect(qrImage(page)).toHaveCount(0);
    await expect(page.getByRole("heading", { level: 2, name: "Sự kiện đã kết thúc" })).toBeVisible();

    // Reopened offline: the ended state from the cached ticket, never a QR; then the ticket and its key are purged.
    await page.reload();
    await expect(page.getByRole("heading", { level: 2, name: "Sự kiện đã kết thúc" })).toBeVisible();
    await expect(qrImage(page)).toHaveCount(0);
    await expect.poll(async () => {
      const state = await offlineState(page);
      return { keys: state.keys, tickets: state.tickets };
    }).toEqual({ keys: [], tickets: [] });
  });

  for (const colorScheme of ["light", "dark"] as const) {
    test(`the offline wallet has no serious accessibility violations (${colorScheme})`, async ({ context, page, baseURL }) => {
      await page.emulateMedia({ colorScheme, reducedMotion: "reduce" });
      const api = await arrange(context, page, baseURL!);
      await openOnlineAndStore(page);

      // (vi) The offline state: the cached code with its hint.
      await goOffline(context, api);
      await page.reload();
      await expect(qrImage(page)).toBeVisible();
      await expect(offlineHint(page)).toBeVisible();

      const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
      const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
      expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target) })), null, 2)).toEqual([]);
    });
  }
});
