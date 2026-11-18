import { afterEach, describe, expect, it, vi } from "vitest";
import { recentSearches } from "./recent-searches";

afterEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
});

describe("recentSearches", () => {
  it("keeps the last five, newest first, without duplicates", () => {
    for (const q of ["a1", "b2", "c3", "d4", "e5", "b2", "f6"]) recentSearches.add(q);

    expect(recentSearches.get()).toEqual(["f6", "b2", "e5", "d4", "c3"]);
  });

  it("ignores blank queries and trims", () => {
    recentSearches.add("   ");
    recentSearches.add("  son tung  ");

    expect(recentSearches.get()).toEqual(["son tung"]);
  });

  it("clears", () => {
    recentSearches.add("son");
    recentSearches.clear();

    expect(recentSearches.get()).toEqual([]);
  });

  it("survives storage that throws or holds garbage", () => {
    localStorage.setItem("tr-recent-searches", "{not json");
    expect(recentSearches.get()).toEqual([]);

    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    expect(() => recentSearches.add("son")).not.toThrow();
    expect(recentSearches.get()).toEqual([]);
  });
});
