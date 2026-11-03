import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { describe, expect, it } from "vitest";
import enServer from "../../messages/en.server.json";
import viServer from "../../messages/vi.server.json";
import vi from "../../messages/vi.json";
import { t } from "./i18n.server";

function keys(value: unknown, prefix = ""): string[] {
  if (typeof value !== "object" || value === null) {
    return [prefix];
  }
  return Object.entries(value).flatMap(([k, v]) => keys(v, prefix ? `${prefix}.${k}` : k));
}

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? sourceFiles(path) : /\.tsx?$/.test(name) && !/\.test\.tsx?$/.test(name) ? [path] : [];
  });
}

describe("server t", () => {
  it("reads server-only copy", () => {
    expect(t("catalog.detail.faqQ1")).toBe("Phòng chờ hoạt động thế nào?");
  });

  it("falls back to the shared catalog", () => {
    expect(t("common.retry")).toBe("Thử lại");
    expect(t("price.from", "vi", { price: "800.000 ₫" })).toBe("từ 800.000 ₫");
  });

  it("returns the key when the copy is missing", () => {
    expect(t("does.not.exist")).toBe("does.not.exist");
  });
});

describe("server message catalogs", () => {
  it("English has exactly the same keys as Vietnamese", () => {
    expect(keys(enServer).sort()).toEqual(keys(viServer).sort());
  });

  it("never repeat a key of the shared catalog (one source per string)", () => {
    const shared = new Set(keys(vi));
    expect(keys(viServer).filter((key) => shared.has(key))).toEqual([]);
  });

  it("are never imported by client components (they would ship every page's copy to the browser)", () => {
    const offenders = sourceFiles(resolve(process.cwd(), "src")).filter((file) => {
      const source = readFileSync(file, "utf8");
      return /^["']use client["']/m.test(source) && /i18n\.server/.test(source);
    });

    expect(offenders).toEqual([]);
  });
});
