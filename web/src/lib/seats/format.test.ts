import { describe, expect, it } from "vitest";
import { seatLabel, seatPlace } from "./format";

const seat = { index: 41, section: "A", row: "3", number: "12", x: 0, y: 0, tierId: "t" };
const plain = (s: string) => s.replace(/ /g, " ");

describe("seatLabel", () => {
  it("reads like the spec's example for a free seat", () => {
    expect(plain(seatLabel(seat, 1_500_000, "free"))).toBe("Khu A, hàng 3, ghế 12, 1.500.000 ₫, còn trống");
  });

  it("names every state", () => {
    expect(seatLabel(seat, 1, "taken")).toMatch(/đã có người chọn$/);
    expect(seatLabel(seat, 1, "selected")).toMatch(/đang chọn$/);
    expect(seatLabel(seat, 1, "mine")).toMatch(/bạn đang giữ$/);
  });
});

describe("seatPlace", () => {
  it("names the section, row and seat for the summary lists", () => {
    expect(seatPlace(seat)).toBe("Khu A · Hàng 3 · Ghế 12");
  });
});
