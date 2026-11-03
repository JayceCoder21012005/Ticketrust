import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { CATEGORIES, categoryName, isCategory } from "./categories";

describe("CATEGORIES", () => {
  it("matches the api's EventCategories, slug for slug and name for name", () => {
    // Vitest runs from web/; the api lives next to it in the same repository.
    const source = readFileSync(resolve(process.cwd(), "../src/TicketRush.Domain/Events/EventCategories.cs"), "utf8");
    const api = [...source.matchAll(/new\("([^"]+)", "([^"]+)"\)/g)].map(([, slug, name]) => ({ slug, name }));

    expect(api.length).toBeGreaterThan(0);
    expect(CATEGORIES).toEqual(api);
  });

  it("names a category and recognises only known slugs", () => {
    expect(categoryName("sports")).toBe("Thể thao");
    expect(categoryName("karaoke")).toBe("karaoke");
    expect(isCategory("concert")).toBe(true);
    expect(isCategory("karaoke")).toBe(false);
    expect(isCategory(undefined)).toBe(false);
  });
});
