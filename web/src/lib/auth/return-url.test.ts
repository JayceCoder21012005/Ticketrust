import { describe, expect, it } from "vitest";
import { safeReturnUrl } from "./return-url";

describe("safeReturnUrl", () => {
  it.each([
    ["/account", "/account"],
    ["/events/abc?tab=seats#map", "/events/abc?tab=seats#map"],
    ["/", "/"],
  ])("keeps same-origin path %s", (input, expected) => {
    expect(safeReturnUrl(input)).toBe(expected);
  });

  it.each([
    "https://evil.com",
    "http://evil.com/account",
    "//evil.com",
    "/\\evil.com",
    "\\\\evil.com",
    "javascript:alert(1)",
    "evil.com",
    "/%2F%2Fevil.com",
    " /account",
    "/acc\nount",
    "",
  ])("rejects %j and falls back to /", (input) => {
    expect(safeReturnUrl(input)).toBe("/");
  });

  it("falls back when there is no returnUrl", () => {
    expect(safeReturnUrl(null)).toBe("/");
    expect(safeReturnUrl(undefined)).toBe("/");
  });

  it("does not send the user back to an auth page", () => {
    expect(safeReturnUrl("/login?returnUrl=/account")).toBe("/");
  });
});
