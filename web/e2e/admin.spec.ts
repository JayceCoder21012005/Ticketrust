import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { signInAs } from "./organizer-fake";

/**
 * Spec 16 on the web against scripted endpoints (the .NET integration tests cover the real ones): anyone but an admin gets the
 * not-found page, the users list filters through the URL, a destructive action asks for a reason and the target typed back, the
 * ⌘K palette jumps by id, and the audit drawer shows the before/after diff.
 */
const users = [
  { id: "0196a1c2-0000-7000-8000-000000000001", email: "an@example.com", fullName: "Nguyễn An", phoneNumber: null, emailConfirmed: true, locked: false, lockoutEnd: null, roles: ["Customer"], createdAt: "2026-11-01T03:00:00Z" },
];

async function axe(page: Page) {
  const { violations } = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
  return violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
}

test("an organizer opening /admin gets the not-found page", async ({ context, page, baseURL }) => {
  await signInAs(context, page, baseURL!, ["Organizer"]);
  await page.goto("/admin/users");
  await expect(page.getByText(/could not be found|không tìm thấy/i)).toBeVisible();
});

test("the users list filters through the url and locking asks for a reason and the email typed back", async ({ context, page, baseURL }) => {
  await signInAs(context, page, baseURL!, ["Admin"]);
  const queries: string[] = [];
  await page.route(/\/api\/v1\/admin\/users(\?.*)?$/, (route) => {
    queries.push(new URL(route.request().url()).searchParams.get("q") ?? "");
    return route.fulfill({ json: { items: users, nextCursor: null } });
  });
  let locked: unknown = null;
  await page.route(/\/api\/v1\/admin\/users\/[^/]+\/lock$/, async (route) => {
    locked = route.request().postDataJSON();
    await route.fulfill({ json: { ...users[0], locked: true } });
  });

  await page.goto("/admin/users?q=an");
  await expect(page.getByRole("heading", { level: 1, name: "Người dùng" })).toBeVisible();
  await expect(page.getByRole("link", { name: "an@example.com" })).toBeVisible();
  expect(queries[0]).toBe("an");
  expect(await axe(page)).toEqual([]);

  await page.getByRole("textbox", { name: "Tìm người dùng" }).fill("binh");
  await page.getByRole("button", { name: "Tìm", exact: true }).click();
  await expect(page).toHaveURL(/\/admin\/users\?q=binh$/);

  await page.getByRole("button", { name: "Thao tác" }).click();
  await page.getByRole("menuitem", { name: "Khoá tài khoản" }).click();
  const dialog = page.getByRole("alertdialog", { name: "Khoá tài khoản" });
  const confirm = dialog.getByRole("button", { name: "Khoá" });
  await dialog.getByLabel("Lý do (ghi vào nhật ký)").fill("Tài khoản bot");
  await expect(confirm).toBeDisabled();
  await dialog.getByLabel(/Nhập/).fill("an@example.com");
  await confirm.click();

  await expect(dialog).toBeHidden();
  expect(locked).toEqual({ reason: "Tài khoản bot" });
});

test("the quick search jumps to a user by id and the audit drawer shows the diff", async ({ context, page, baseURL }) => {
  await signInAs(context, page, baseURL!, ["Admin"]);
  await page.route(/\/api\/v1\/admin\/users(\?.*)?$/, (route) => route.fulfill({ json: { items: users, nextCursor: null } }));
  await page.route(/\/api\/v1\/admin\/audit(\?.*)?$/, (route) =>
    route.fulfill({
      json: {
        items: [
          {
            id: "a1",
            at: "2026-11-22T03:00:00Z",
            actorUserId: null,
            actorIp: null,
            action: "ticket.updated",
            entityType: "ticket",
            entityId: "t1",
            changes: JSON.stringify({ status: { before: "Valid", after: "Listed" } }),
            correlationId: "c1",
          },
        ],
        nextCursor: null,
      },
    }),
  );

  await page.goto("/admin/audit");
  await page.getByRole("button", { name: "Xem" }).click();
  const drawer = page.getByRole("dialog", { name: "ticket.updated" });
  await expect(drawer.getByText("Valid")).toBeVisible();
  await expect(drawer.getByText("Listed")).toBeVisible();
  expect(await axe(page)).toEqual([]);
  await page.keyboard.press("Escape");

  await page.getByRole("button", { name: /Tìm nhanh/ }).click();
  await page.getByPlaceholder("Id hoặc email…").fill(users[0]!.id);
  await page.getByRole("option", { name: `Người dùng ${users[0]!.id}` }).click();
  await expect(page).toHaveURL(new RegExp(`/admin/users/${users[0]!.id}$`));
});
