import { describe, expect, it } from "vitest";
import { qrPath, qrViewBoxSize, QUIET_ZONE } from "./qr-svg";

describe("qrPath", () => {
  it("draws each run of dark modules as one rectangle, inside a 4-module quiet zone", () => {
    const matrix = [
      [true, true, false],
      [false, true, true],
      [true, false, true],
    ];

    expect(QUIET_ZONE).toBe(4);
    expect(qrViewBoxSize(matrix)).toBe(11);
    expect(qrPath(matrix)).toBe("M4 4h2v1h-2zM5 5h2v1h-2zM4 6h1v1h-1zM6 6h1v1h-1z");
  });

  it("draws nothing for an all-light matrix", () => {
    expect(qrPath([[false, false]])).toBe("");
  });
});
