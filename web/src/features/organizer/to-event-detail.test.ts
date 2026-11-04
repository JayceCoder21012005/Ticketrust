import { describe, expect, it } from "vitest";
import { toEventDetail } from "./to-event-detail";
import { eventFixture } from "./wizard/steps.fixtures";

describe("toEventDetail", () => {
  it("maps a draft into what the public detail page renders", () => {
    const detail = toEventDetail(
      eventFixture({
        tiers: [{ id: "t1", name: "VIP", price: 900_000, kind: "Seated", capacity: null, color: "#7c3aed" }],
        phases: [{ id: "p1", name: "Presale", opensAt: "a", closesAt: "b", queueEnabled: true, maxTicketsPerUser: 2, hasAccessCode: true }],
      }),
      { id: "v1", name: "Nhà hát Lớn", address: "1 Tràng Tiền", city: "Hà Nội", mapUrl: null, capacity: 600, shared: true },
    );

    expect(detail.venue).toEqual({ id: "v1", name: "Nhà hát Lớn", address: "1 Tràng Tiền", city: "Hà Nội", mapUrl: null });
    expect(detail.tiers).toEqual([{ id: "t1", name: "VIP", price: 900_000, currency: "VND", kind: "Seated", color: "#7c3aed" }]);
    expect(detail.phases[0].requiresAccessCode).toBe(true);
    expect(detail.isSoldOut).toBe(false);
  });

  it("still renders when the venue isn't loaded yet", () => {
    expect(toEventDetail(eventFixture(), undefined).venue.name).toBe("Địa điểm");
  });
});
