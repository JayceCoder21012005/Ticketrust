import { describe, expect, it } from "vitest";
import { ApiError, NetworkError } from "@/lib/errors";
import type { ScanResponse } from "./api";
import { describeFailure, describeScan, seatLabel } from "./describe-scan";

const ticket = { tier: "VIP", section: "A", row: "3", seat: "12", holderName: "Lê Thị Hương" };
const base: ScanResponse = { result: "Admitted", reason: null, ticket, checkedInAt: null, gateName: null };

describe("describeScan", () => {
  it("shows an admitted ticket green with the seat and the holder", () => {
    expect(describeScan(base)).toEqual({ tone: "ok", title: "Hợp lệ", headline: "Khu A · Hàng 3 · Ghế 12", lines: ["Lê Thị Hương", "VIP"] });
  });

  it("names the tier for general admission", () => {
    expect(seatLabel({ ...ticket, section: null, row: null, seat: null })).toBe("VIP");
  });

  it("shows an already checked-in ticket amber with the Vietnam time and the gate", () => {
    const view = describeScan({ ...base, result: "AlreadyCheckedIn", checkedInAt: "2026-11-20T12:42:00Z", gateName: "Cổng B" });
    expect(view.tone).toBe("warn");
    expect(view.title).toBe("Đã check-in lúc 19:42 – Cổng B");
  });

  it.each([
    ["Invalid", "Mã không hợp lệ"],
    ["Expired", "Mã đã hết hạn — nhờ khách mở lại vé"],
    ["WrongEvent", "Vé của sự kiện khác"],
    ["NotValid", "Vé không còn hiệu lực"],
  ] as const)("shows %s red", (result, title) => {
    expect(describeScan({ ...base, result, ticket: null })).toMatchObject({ tone: "bad", title });
  });

  it("explains a refusal with its reason", () => {
    expect(describeScan({ ...base, result: "NotValid", reason: "voided", ticket: null }).lines).toEqual(["Vé đã bị huỷ"]);
  });
});

describe("describeFailure", () => {
  it("says the ticket was not checked when the network is down", () => {
    const view = describeFailure(new NetworkError());
    expect(view.tone).toBe("bad");
    expect(view.lines[0]).toContain("CHƯA");
  });

  it("tells staff to slow down on 429 and that they are not assigned on 404", () => {
    expect(describeFailure(new ApiError(429, {})).title).toBe("Quét quá nhanh");
    expect(describeFailure(new ApiError(404, {})).title).toContain("không được phân công");
  });
});
