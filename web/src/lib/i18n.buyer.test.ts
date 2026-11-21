import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { describe, expect, it } from "vitest";
import enBuyer from "../../messages/en.buyer.json";
import viBuyer from "../../messages/vi.buyer.json";
import enServer from "../../messages/en.server.json";
import viServer from "../../messages/vi.server.json";
import vi from "../../messages/vi.json";
import { t } from "./i18n.buyer";
import { t as serverT } from "./i18n.server";

function keys(value: unknown, prefix = ""): string[] {
  if (typeof value !== "object" || value === null) {
    return [prefix];
  }
  return Object.entries(value).flatMap(([k, v]) => keys(v, prefix ? `${prefix}.${k}` : k));
}

const SRC = resolve(process.cwd(), "src");
const posix = (path: string) => relative(SRC, path).split("\\").join("/");

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? sourceFiles(path) : /\.tsx?$/.test(name) && !/\.test\.tsx?$/.test(name) ? [path] : [];
  });
}

/** The namespaces that live in `*.buyer.json`: only the buyer area renders them. */
const NAMESPACES = Object.keys(viBuyer);
const namespacePattern = NAMESPACES.join("|");
const sources = sourceFiles(SRC).map((path) => ({ path: posix(path), text: readFileSync(path, "utf8") }));
/** `"tickets.title"`, `'queue.x'` or a template that starts with a buyer namespace (`queue.${key}`). */
const buyerKey = new RegExp("[\"'`](?:" + namespacePattern + ")[.][A-Za-z$]");
const importsShared = /from "@\/lib\/i18n";/;
const importsBuyerOrServer = /from "@\/lib\/i18n\.(?:buyer|server)";/;

describe("buyer t", () => {
  it("reads buyer-area copy", () => {
    expect(t("tickets.title")).toBe(viBuyer.tickets.title);
    expect(t("queue.closed.title")).toBe(viBuyer.queue.closed.title);
  });

  it("falls back to the shared catalog", () => {
    expect(t("common.retry")).toBe("Thử lại");
    expect(t("price.from", "vi", { price: "800.000 ₫" })).toBe("từ 800.000 ₫");
  });

  it("interpolates {placeholders}", () => {
    expect(t("tickets.shown", "vi", { tab: "Sắp diễn ra", count: 3 })).toContain("3");
    expect(t("tickets.shown", "vi", { tab: "Sắp diễn ra", count: 3 })).toContain("Sắp diễn ra");
  });

  it("returns the key when the copy is missing", () => {
    expect(t("tickets.does.not.exist")).toBe("tickets.does.not.exist");
  });

  it("is what the server t falls back to, so buyer pages can use the server t for everything", () => {
    expect(serverT("tickets.title")).toBe(viBuyer.tickets.title);
    expect(serverT("common.retry")).toBe("Thử lại");
    expect(serverT("catalog.detail.faqQ1")).toBe("Phòng chờ hoạt động thế nào?");
  });
});

describe("buyer message catalogs", () => {
  it("hold the eight buyer namespaces", () => {
    expect(NAMESPACES).toEqual(["tickets", "checkout", "queue", "orders", "seats", "accountHub", "waitlist", "resale"]);
  });

  it("English has exactly the same keys as Vietnamese", () => {
    expect(keys(enBuyer).sort()).toEqual(keys(viBuyer).sort());
  });

  it("never repeat a key of the shared or the server catalog (one source per string)", () => {
    const others = new Set([...keys(vi), ...keys(viServer), ...keys(enServer)]);
    expect(keys(viBuyer).filter((key) => others.has(key))).toEqual([]);
    expect(Object.keys(vi).filter((namespace) => NAMESPACES.includes(namespace))).toEqual([]);
  });
});

describe("where the buyer catalog is imported", () => {
  it("every file that renders a buyer key imports the buyer or the server t, never the shared one", () => {
    // A missed file would render the raw key. Also covers dynamic prefixes (`${key}.title` with key = "queue.…").
    const offenders = sources.filter(({ text }) => buyerKey.test(text) && importsShared.test(text)).map(({ path }) => path);

    expect(offenders).toEqual([]);
    const callsBuyerKey = new RegExp(String.raw`(?<![\w.])t\(\s*["'` + "`" + String.raw`](?:${namespacePattern})\.`);
    const calling = sources.filter(({ text }) => callsBuyerKey.test(text));
    expect(calling.length).toBeGreaterThan(20);
    expect(calling.filter(({ text }) => !importsBuyerOrServer.test(text)).map(({ path }) => path)).toEqual([]);
  });

  it("files that turn the order helpers' keys into text (they return buyer keys) use the buyer t", () => {
    const helpers = /from "@\/lib\/checkout\/order-(?:state|timeline)"/;
    const offenders = sources.filter(({ text }) => helpers.test(text) && importsShared.test(text)).map(({ path }) => path);

    expect(offenders).toEqual([]);
  });

  it("the public, auth, shared and catalog code never imports it (it would ship the buyer copy to every page)", () => {
    const publicAreas = [/^app\/\(public\)\//, /^components\/shared\//, /^features\/(?:catalog|auth|notifications|mock-gateway|organizer)\//];
    const offenders = sources
      .filter(({ path, text }) => publicAreas.some((area) => area.test(path)) && /i18n\.buyer/.test(text))
      // The shopping timer is mounted only by the buyer layout.
      .filter(({ path }) => path !== "components/shared/shopping-timer.tsx")
      .map(({ path }) => path);

    expect(offenders).toEqual([]);
  });

  it("client components never import the buyer catalog through the server t", () => {
    const offenders = sources.filter(({ text }) => /^["']use client["']/m.test(text) && /i18n\.server/.test(text)).map(({ path }) => path);

    expect(offenders).toEqual([]);
  });
});
