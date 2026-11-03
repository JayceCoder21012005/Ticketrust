import { describe, expect, it } from "vitest";
import { externalHttpUrl } from "./safe-url";

describe("externalHttpUrl", () => {
  it("keeps http and https links", () => {
    expect(externalHttpUrl("https://maps.google.com/?q=My+Dinh")).toBe("https://maps.google.com/?q=My+Dinh");
    expect(externalHttpUrl("http://example.com/a")).toBe("http://example.com/a");
  });

  it.each(["javascript:alert(1)", " JavaScript:alert(1)", "data:text/html,<script>", "//evil.com", "/relative", "not a url", "", null])(
    "drops %j",
    (value) => {
      expect(externalHttpUrl(value)).toBeNull();
    },
  );
});
