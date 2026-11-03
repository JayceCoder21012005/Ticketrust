import { describe, expect, it } from "vitest";
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

  it("returns English copy when asked", () => {
    expect(t("common.retry", "en")).toBe("Try again");
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
