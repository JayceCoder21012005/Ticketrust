import { describe, expect, it } from "vitest";
import { isSafeRedirect } from "./redirect";

describe("isSafeRedirect", () => {
  it.each(["https://pay.test/s/1", "http://localhost:3100/mock-gateway/s1"])("accepts %s", (url) => expect(isSafeRedirect(url)).toBe(true));

  it.each(["javascript:alert(1)", "data:text/html,x", "ftp://pay.test/s", "//pay.test/s", "/relative", "not a url", ""])("rejects %s", (url) =>
    expect(isSafeRedirect(url)).toBe(false),
  );
});
