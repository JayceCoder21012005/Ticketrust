import { describe, expect, it } from "vitest";
import { activeFilterCount, filtersToSearchParams, parseFilters, toApiQuery } from "./filters";

describe("parseFilters", () => {
  it("reads every known filter from the URL and ignores the rest", () => {
    const filters = parseFilters(
      new URLSearchParams("q=son+tung&city=H%C3%A0+N%E1%BB%99i&category=concert&when=weekend&priceMax=1000000&utm_source=x"),
    );

    expect(filters).toEqual({ q: "son tung", city: "Hà Nội", category: "concert", when: "weekend", priceMax: 1_000_000 });
  });

  it("accepts Next's searchParams object (string | string[] values)", () => {
    expect(parseFilters({ q: ["a", "b"], city: undefined, from: "2026-12-01" })).toEqual({ q: "a", from: "2026-12-01" });
  });

  it("drops invalid values instead of failing", () => {
    expect(parseFilters(new URLSearchParams("when=someday&priceMax=-5&from=12/01/2026&to=2026-13-40&q=%20%20"))).toEqual({});
  });

  it("caps the search text at 100 characters like the api", () => {
    expect(parseFilters(new URLSearchParams(`q=${"a".repeat(150)}`)).q).toHaveLength(100);
  });
});

describe("filtersToSearchParams", () => {
  it("round-trips through parseFilters with a stable key order and no empty keys", () => {
    const filters = { priceMax: 500_000, q: "đen vâu", category: "concert", city: "Hồ Chí Minh", from: "2026-12-01", to: "2026-12-31" };

    const params = filtersToSearchParams(filters);

    expect([...params.keys()]).toEqual(["q", "category", "city", "from", "to", "priceMax"]);
    expect(parseFilters(params)).toEqual(filters);
  });

  it("returns empty params when nothing is filtered", () => {
    expect(filtersToSearchParams({}).toString()).toBe("");
  });
});

describe("activeFilterCount", () => {
  it("counts filters but not the search text", () => {
    expect(activeFilterCount({ q: "x", city: "Hà Nội", when: "today", priceMax: 1 })).toBe(3);
  });
});

describe("toApiQuery", () => {
  // Saturday 14 Nov 2026, 10:00 in Vietnam.
  const now = new Date("2026-11-14T03:00:00Z");

  it("turns custom dates into whole Vietnam days", () => {
    expect(toApiQuery({ from: "2026-12-01", to: "2026-12-02" }, now)).toEqual({
      from: "2026-12-01T00:00:00+07:00",
      to: "2026-12-02T23:59:59.999+07:00",
    });
  });

  it("'today' is the rest of the current Vietnam day", () => {
    expect(toApiQuery({ when: "today" }, now)).toEqual({ from: "2026-11-14T00:00:00+07:00", to: "2026-11-14T23:59:59.999+07:00" });
  });

  it("'weekend' on a Saturday covers Saturday and Sunday", () => {
    expect(toApiQuery({ when: "weekend" }, now)).toEqual({ from: "2026-11-14T00:00:00+07:00", to: "2026-11-15T23:59:59.999+07:00" });
  });

  it("'weekend' on a Wednesday is the coming Saturday and Sunday", () => {
    expect(toApiQuery({ when: "weekend" }, new Date("2026-11-11T03:00:00Z"))).toEqual({
      from: "2026-11-14T00:00:00+07:00",
      to: "2026-11-15T23:59:59.999+07:00",
    });
  });

  it("'month' runs to the end of the current month", () => {
    expect(toApiQuery({ when: "month" }, now)).toEqual({ from: "2026-11-14T00:00:00+07:00", to: "2026-11-30T23:59:59.999+07:00" });
  });

  it("passes the other filters through and stringifies the price", () => {
    expect(toApiQuery({ q: "a", city: "Hà Nội", category: "sports", priceMax: 300_000 }, now)).toEqual({
      q: "a",
      city: "Hà Nội",
      category: "sports",
      priceMax: "300000",
    });
  });

  it("a preset wins over custom dates", () => {
    expect(toApiQuery({ when: "today", from: "2027-01-01" }, now).from).toBe("2026-11-14T00:00:00+07:00");
  });
});
