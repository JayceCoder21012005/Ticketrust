import { describe, expect, it } from "vitest";
import { ApiError, toFriendlyMessage } from "./errors";

describe("toFriendlyMessage", () => {
  it("maps a known ProblemDetails code to its Vietnamese message", () => {
    const error = new ApiError(409, { code: "seat.already_held", title: "Seat is already held." });

    expect(toFriendlyMessage(error)).toBe("Ghế này vừa có người giữ. Vui lòng chọn ghế khác.");
  });

  it("falls back to a message per HTTP status for unknown codes", () => {
    expect(toFriendlyMessage(new ApiError(429, { code: "something.new" }))).toMatch(/quá nhiều/i);
    expect(toFriendlyMessage(new ApiError(503, {}))).toMatch(/bận/i);
  });

  it("never shows raw server titles for server errors", () => {
    const error = new ApiError(500, { code: "server.error", title: "NullReferenceException at Foo" });

    expect(toFriendlyMessage(error)).not.toContain("NullReference");
  });

  it("treats non-API failures (network down) as a connection problem", () => {
    expect(toFriendlyMessage(new TypeError("Failed to fetch"))).toMatch(/kết nối/i);
  });
});

describe("ApiError", () => {
  it("exposes the problem code and status", () => {
    const error = new ApiError(404, { code: "event.not_found" });

    expect(error.code).toBe("event.not_found");
    expect(error.status).toBe(404);
  });
});
