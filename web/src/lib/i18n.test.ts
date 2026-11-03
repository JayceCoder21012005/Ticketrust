import { describe, expect, it, vi as vitest } from "vitest";
import en from "../../messages/en.json";
import vi from "../../messages/vi.json";
import { t } from "./i18n";

function keys(value: unknown, prefix = ""): string[] {
  if (typeof value !== "object" || value === null) {
    return [prefix];
  }
  return Object.entries(value).flatMap(([k, v]) => keys(v, prefix ? `${prefix}.${k}` : k));
}

describe("t", () => {
  it("returns Vietnamese copy by default", () => {
    expect(t("common.retry")).toBe("Thử lại");
  });

  it("never loads the English catalog: every client component using t() would ship it to the browser", async () => {
    vitest.resetModules();
    vitest.doMock("../../messages/en.json", () => {
      throw new Error("en.json was loaded");
    });
    try {
      const { t: fresh } = await import("./i18n");
      expect(fresh("common.retry")).toBe("Thử lại");
    } finally {
      vitest.doUnmock("../../messages/en.json");
    }
  });

  it("interpolates {placeholders}", () => {
    expect(t("price.from", "vi", { price: "800.000 ₫" })).toBe("từ 800.000 ₫");
  });

  it("returns the key itself when the copy is missing, so gaps are visible", () => {
    expect(t("does.not.exist")).toBe("does.not.exist");
  });
});

describe("message catalogs", () => {
  it("English has exactly the same keys as Vietnamese", () => {
    expect(keys(en).sort()).toEqual(keys(vi).sort());
  });
});
