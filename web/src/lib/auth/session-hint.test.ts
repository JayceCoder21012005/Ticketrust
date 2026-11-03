import { describe, expect, it } from "vitest";
import { hasSessionHint } from "./session-hint";

describe("hasSessionHint", () => {
  it("is true only when the tr_session=1 cookie is present", () => {
    expect(hasSessionHint("theme=dark; tr_session=1")).toBe(true);
    expect(hasSessionHint("tr_session=1")).toBe(true);
    expect(hasSessionHint("")).toBe(false);
    expect(hasSessionHint("tr_session=")).toBe(false);
    expect(hasSessionHint("xtr_session=1")).toBe(false);
  });
});
