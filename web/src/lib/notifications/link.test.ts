import { describe, expect, it } from "vitest";
import { safeLink } from "./link";

describe("safeLink", () => {
  it.each(["/account/orders/abc", "/events/dem-nhac?x=1#top", "/"])("keeps the app-relative path %s", (link) => {
    expect(safeLink(link)).toBe(link);
  });

  it.each([
    "https://evil.example/phish",
    "http://evil.example",
    "//evil.example/x",
    "/\\evil.example",
    "\\\\evil.example",
    "/\t/evil.example",
    "/\n/evil.example",
    "javascript:alert(1)",
    "events/dem-nhac",
    " /events",
    "",
  ])("rejects %j", (link) => {
    expect(safeLink(link)).toBeNull();
  });

  it("rejects a missing link", () => {
    expect(safeLink(null)).toBeNull();
  });
});
