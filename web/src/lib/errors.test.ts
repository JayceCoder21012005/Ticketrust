import { describe, expect, it } from "vitest";
import { ApiError, NetworkError, toFriendlyMessage } from "./errors";

describe("toFriendlyMessage", () => {
  it("maps a known ProblemDetails code to its Vietnamese message", () => {
    const error = new ApiError(409, { code: "seat.already_held", title: "Seat is already held." });

    expect(toFriendlyMessage(error)).toBe("Ghế này vừa có người giữ. Vui lòng chọn ghế khác.");
  });

  it("explains why the event can't start before a sale phase closes", () => {
    expect(toFriendlyMessage(new ApiError(409, { code: "phase.after_event_start" }))).toBe("Các đợt mở bán phải kết thúc trước khi sự kiện bắt đầu.");
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
    expect(toFriendlyMessage(new NetworkError({ cause: new TypeError("Failed to fetch") }))).toMatch(/kết nối/i);
  });
});

describe("spec 03 codes", () => {
  it.each([
    "auth.invalid_credentials",
    "auth.email_not_confirmed",
    "auth.locked_out",
    "auth.refresh_invalid",
    "auth.weak_password",
    "auth.invalid_token",
    "auth.csrf_header_missing",
    "user.avatar_invalid",
    "user.not_found",
    "storage.upload_forbidden",
    "storage.too_large",
    "storage.content_type_mismatch",
    "validation.failed",
  ])("%s has its own Vietnamese message", (code) => {
    const message = toFriendlyMessage(new ApiError(418, { code }));

    expect(message).not.toBe(toFriendlyMessage(new ApiError(418, {})));
  });
});

describe("ApiError", () => {
  it("exposes the problem code and status", () => {
    const error = new ApiError(404, { code: "event.not_found" });

    expect(error.code).toBe("event.not_found");
    expect(error.status).toBe(404);
  });
});
