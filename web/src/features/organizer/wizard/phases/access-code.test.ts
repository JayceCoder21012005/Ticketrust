import { describe, expect, it } from "vitest";
import { generateAccessCode } from "./access-code";

describe("generateAccessCode", () => {
  it("makes 8 characters people can read out loud: no 0/O or 1/I", () => {
    for (let i = 0; i < 200; i++) {
      expect(generateAccessCode()).toMatch(/^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{8}$/);
    }
  });

  it("uses the injected random source", () => {
    const zeros = (array: Uint8Array) => array.fill(0);

    expect(generateAccessCode(zeros)).toBe("AAAAAAAA");
  });
});
