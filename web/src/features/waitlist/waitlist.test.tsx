import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";

const router = vi.hoisted(() => ({ push: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router, usePathname: () => "/" }));

const api = vi.hoisted(() => ({ join: vi.fn(), offer: vi.fn(), accept: vi.fn(), list: vi.fn() }));
vi.mock("@/lib/waitlist/api", () => ({
  joinWaitlist: api.join,
  getWaitlistOffer: api.offer,
  acceptWaitlistOffer: api.accept,
  listMyWaitlist: api.list,
  getMyWaitlistEntry: vi.fn(),
  leaveWaitlist: vi.fn(),
}));

import { MyWaitlist } from "./my-waitlist";
import { OfferView } from "./offer-view";
import { JoinDialog } from "./waitlist-control";

const tiers = [
  { id: "vip", name: "VIP", price: 2_000_000, currency: "VND", kind: "Seated" as const, color: "#000" },
  { id: "ga", name: "Đứng", price: 800_000, currency: "VND", kind: "GeneralAdmission" as const, color: "#000" },
];

const entry = (over: object = {}) => ({
  entryId: "w1",
  eventId: "e1",
  status: "Waiting",
  position: 3,
  tierId: null,
  quantity: 2,
  joinedAt: "2026-11-19T03:00:00Z",
  offer: null,
  ...over,
});

const conflict = (code: string) => new ApiError(409, { code, title: code });

afterEach(() => vi.clearAllMocks());

describe("JoinDialog", () => {
  const renderDialog = (onJoined = vi.fn()) => {
    render(<JoinDialog open onOpenChange={vi.fn()} eventId="e1" tiers={tiers} maxQuantity={3} onJoined={onJoined} />);
    return onJoined;
  };

  it("joins for any tier by default with the chosen quantity, never above the cap", async () => {
    api.join.mockResolvedValue(entry());
    const onJoined = renderDialog();

    expect(screen.getByRole("combobox", { name: "Hạng vé" })).toHaveValue("");
    expect(screen.getByRole("option", { name: "Hạng bất kỳ" })).toBeInTheDocument();
    const more = screen.getByRole("button", { name: "Thêm một vé" });
    fireEvent.click(more);
    fireEvent.click(more);
    expect(more).toBeDisabled();
    expect(screen.getByText("3")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Bớt một vé" }));
    fireEvent.click(screen.getByRole("button", { name: "Vào danh sách chờ" }));

    await waitFor(() => expect(onJoined).toHaveBeenCalled());
    expect(api.join).toHaveBeenCalledWith("e1", null, 2);
  });

  it("joins for the chosen tier and explains how offers work", async () => {
    api.join.mockResolvedValue(entry());
    renderDialog();

    expect(screen.getByText("Khi có vé, bạn có 15 phút để thanh toán.")).toBeInTheDocument();
    fireEvent.change(screen.getByRole("combobox", { name: "Hạng vé" }), { target: { value: "ga" } });
    fireEvent.click(screen.getByRole("button", { name: "Vào danh sách chờ" }));

    await waitFor(() => expect(api.join).toHaveBeenCalledWith("e1", "ga", 1));
  });

  it("says why the api refused", async () => {
    api.join.mockRejectedValue(conflict("waitlist.not_available"));
    const onJoined = renderDialog();

    fireEvent.click(screen.getByRole("button", { name: "Vào danh sách chờ" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Sự kiện vẫn còn vé, bạn có thể mua ngay.");
    expect(onJoined).not.toHaveBeenCalled();
  });
});

describe("OfferView", () => {
  const offer = {
    offerId: "w1",
    eventId: "e1",
    eventSlug: "son-tung-mtp-live",
    eventTitle: "Sơn Tùng M-TP Live",
    startsAt: "2026-12-20T13:00:00Z",
    expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
    hold: {
      holdId: "h1",
      seats: [{ index: 5, section: "A", row: "1", number: "6", tierId: "vip", price: 2_000_000 }],
      ga: [],
      total: 2_000_000,
      expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
      serverNow: new Date().toISOString(),
    },
  };

  beforeEach(() => {
    api.offer.mockResolvedValue(offer);
  });

  it("shows the held ticket, the countdown and continues in checkout", async () => {
    api.accept.mockResolvedValue({ holdId: "h1", eventId: "e1", eventSlug: "son-tung-mtp-live", expiresAt: offer.expiresAt });
    render(<OfferView offerId="w1" />);

    expect(await screen.findByRole("heading", { level: 1, name: "Có vé cho bạn!" })).toBeInTheDocument();
    expect(screen.getByRole("timer")).toHaveTextContent("Thời gian giữ vé");
    expect(screen.getByText("Sơn Tùng M-TP Live")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Mua ngay" }));

    await waitFor(() => expect(router.push).toHaveBeenCalledWith("/events/son-tung-mtp-live/checkout"));
  });

  it("says the offer moved on when it is no longer live", async () => {
    api.offer.mockImplementation(() => Promise.reject(conflict("waitlist.offer_expired")));
    render(<OfferView offerId="w1" />);

    expect(await screen.findByRole("heading", { name: "Lời mời đã hết hạn" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Về danh sách chờ của tôi" })).toHaveAttribute("href", "/account/waitlist");
  });
});

describe("MyWaitlist", () => {
  it("lists each entry with its place in line, or the link to its offer", async () => {
    api.list.mockResolvedValue([
      { entry: entry(), eventSlug: "a", eventTitle: "Sự kiện A", startsAt: "2026-12-01T13:00:00Z", city: "Hà Nội", coverImageUrl: null, tierName: null },
      {
        entry: entry({ entryId: "w2", status: "Offered", position: null, offer: { offerId: "w2", expiresAt: "2026-11-19T03:15:00Z" } }),
        eventSlug: "b",
        eventTitle: "Sự kiện B",
        startsAt: "2026-12-02T13:00:00Z",
        city: "Đà Nẵng",
        coverImageUrl: null,
        tierName: "VIP",
      },
    ]);
    render(<MyWaitlist />);

    expect(await screen.findByText("Đang chờ · vị trí #3")).toBeInTheDocument();
    expect(screen.getByText(/Hạng bất kỳ · 2 vé/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Xem lời mời" })).toHaveAttribute("href", "/waitlist/offers/w2");
  });

  it("says what to do when the buyer waits for nothing", async () => {
    api.list.mockResolvedValue([]);
    render(<MyWaitlist />);

    expect(await screen.findByText("Bạn chưa ở danh sách chờ nào.")).toBeInTheDocument();
  });
});
