import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/errors";
import type { TicketDetail } from "@/lib/tickets/types";

const router = vi.hoisted(() => ({ push: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router, usePathname: () => "/" }));

const api = vi.hoisted(() => ({
  createListing: vi.fn(),
  cancelListing: vi.fn(),
  createTransfer: vi.fn(),
  cancelTransfer: vi.fn(),
  listEventListings: vi.fn(),
  reserveListing: vi.fn(),
  getTransfer: vi.fn(),
  acceptTransfer: vi.fn(),
}));
vi.mock("@/lib/resale/api", () => api);
vi.mock("@/lib/auth/session-hint", () => ({ hasSessionHint: () => true }));
vi.mock("@/lib/api/session", () => ({ getSession: () => ({ accessToken: "x" }), refreshAccessToken: vi.fn() }));

import ResaleListings from "./resale-listings";
import { ResaleSheet, TicketActions, TransferDialog } from "./ticket-actions";
import { TransferAcceptView } from "./transfer-accept";

afterEach(() => vi.clearAllMocks());

const ticket = (resale: Partial<TicketDetail["resale"]> = {}, status: TicketDetail["status"] = "Valid") =>
  ({
    id: "t1",
    status,
    resale: { faceValue: 2_000_000, currency: "VND", canChange: true, listing: null, transfer: null, ...resale },
  }) as unknown as TicketDetail;

describe("ResaleSheet", () => {
  it("starts at face value, never accepts more, and lists at the chosen price", async () => {
    api.createListing.mockResolvedValue({});
    const onDone = vi.fn();
    render(<ResaleSheet open onOpenChange={vi.fn()} ticketId="t1" faceValue={2_000_000} onDone={onDone} />);

    const price = screen.getByRole("spinbutton", { name: "Giá bán" });
    expect(price).toHaveValue(2_000_000);
    expect(screen.getByText(/Bạn nhận: 2\.000\.000/)).toBeInTheDocument();
    expect(screen.getByText("10 phút đầu, vé được ưu tiên cho người trong danh sách chờ.")).toBeInTheDocument();

    fireEvent.change(price, { target: { value: "2000001" } });
    expect(screen.getByRole("button", { name: "Đăng bán" })).toBeDisabled();

    fireEvent.change(price, { target: { value: "1500000" } });
    expect(screen.getByText(/Bạn nhận: 1\.500\.000/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Đăng bán" }));

    await waitFor(() => expect(onDone).toHaveBeenCalled());
    expect(api.createListing).toHaveBeenCalledWith("t1", 1_500_000);
  });

  it("shows the api's reason when the listing is refused", async () => {
    api.createListing.mockRejectedValue(new ApiError(409, { code: "resale.not_allowed", title: "x" }));
    render(<ResaleSheet open onOpenChange={vi.fn()} ticketId="t1" faceValue={1_000_000} onDone={vi.fn()} />);

    fireEvent.click(screen.getByRole("button", { name: "Đăng bán" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("không thể bán lại");
  });
});

describe("TransferDialog", () => {
  it("warns that the current ticket dies and sends to the trimmed email", async () => {
    api.createTransfer.mockResolvedValue({});
    const onDone = vi.fn();
    render(<TransferDialog open onOpenChange={vi.fn()} ticketId="t1" onDone={onDone} />);

    expect(screen.getByText("Vé hiện tại sẽ mất hiệu lực sau khi người nhận chấp nhận")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Email người nhận"), { target: { value: "  ban@example.com " } });
    fireEvent.click(screen.getByRole("button", { name: "Gửi vé" }));

    await waitFor(() => expect(onDone).toHaveBeenCalled());
    expect(api.createTransfer).toHaveBeenCalledWith("t1", "ban@example.com");
  });

  it("says why a transfer to yourself is refused", async () => {
    api.createTransfer.mockRejectedValue(new ApiError(400, { code: "transfer.self", title: "x" }));
    render(<TransferDialog open onOpenChange={vi.fn()} ticketId="t1" onDone={vi.fn()} />);
    fireEvent.change(screen.getByLabelText("Email người nhận"), { target: { value: "me@example.com" } });
    fireEvent.click(screen.getByRole("button", { name: "Gửi vé" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Bạn không thể chuyển vé cho chính mình.");
  });
});

describe("TicketActions", () => {
  it("is hidden for a ticket that can no longer change hands", () => {
    const { container } = render(<TicketActions ticket={ticket({ canChange: false })} onChanged={vi.fn()} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("shows the live listing and takes it back", async () => {
    api.cancelListing.mockResolvedValue(undefined);
    const onChanged = vi.fn();
    render(
      <TicketActions
        ticket={ticket({ canChange: false, listing: { listingId: "l1", price: 1_200_000, status: "Active", waitlistUntil: null } }, "Listed")}
        onChanged={onChanged}
      />,
    );

    expect(screen.getByText(/Đang bán lại với giá 1\.200\.000/)).toBeInTheDocument();
    fireEvent.pointerDown(screen.getByRole("button", { name: "Tuỳ chọn vé" }), { button: 0, ctrlKey: false });
    fireEvent.click(await screen.findByRole("menuitem", { name: "Huỷ bán lại" }));

    await waitFor(() => expect(onChanged).toHaveBeenCalled());
    expect(api.cancelListing).toHaveBeenCalledWith("l1");
  });
});

describe("ResaleListings", () => {
  const groups = [
    {
      tierId: "vip",
      tierName: "VIP",
      color: "#f00",
      faceValue: 2_000_000,
      listings: [{ listingId: "l1", seatLabel: "A · 1 · 6", price: 1_800_000, currency: "VND" }],
    },
  ];

  it("lists resale tickets by tier and reserves one into checkout", async () => {
    api.listEventListings.mockResolvedValue(groups);
    api.reserveListing.mockResolvedValue({ id: "o1" });
    render(<ResaleListings eventId="e1" slug="show" />);

    expect(await screen.findByRole("heading", { name: "Vé bán lại" })).toBeInTheDocument();
    expect(screen.getByText("Ghế A · 1 · 6")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Mua" }));

    await waitFor(() => expect(router.push).toHaveBeenCalledWith("/checkout/o1"));
    expect(api.reserveListing).toHaveBeenCalledWith("l1", expect.any(String));
  });

  it("renders nothing when there are no listings", async () => {
    api.listEventListings.mockResolvedValue([]);
    const { container } = render(<ResaleListings eventId="e1" slug="show" />);

    await waitFor(() => expect(api.listEventListings).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });

  it("explains a listing someone else just took and refreshes the list", async () => {
    api.listEventListings.mockResolvedValue(groups);
    api.reserveListing.mockRejectedValue(new ApiError(409, { code: "resale.listing_unavailable", title: "x" }));
    render(<ResaleListings eventId="e1" slug="show" />);

    fireEvent.click(await screen.findByRole("button", { name: "Mua" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("vừa có người mua");
    await waitFor(() => expect(api.listEventListings).toHaveBeenCalledTimes(2));
  });
});

describe("TransferAcceptView", () => {
  const invite = (over: object = {}) => ({
    transferId: "x1",
    status: "Pending",
    expiresAt: "2026-12-01T10:00:00Z",
    fromName: "Trần Bình",
    eventId: "e1",
    eventSlug: "show",
    eventTitle: "Đêm nhạc",
    startsAt: "2026-12-20T13:00:00Z",
    venue: "Nhà hát",
    tierName: "VIP",
    seatLabel: "A · 1 · 6",
    newTicketId: null,
    ...over,
  });

  it("accepts the ticket and links to the new one", async () => {
    api.getTransfer.mockResolvedValue(invite());
    api.acceptTransfer.mockResolvedValue(invite({ status: "Accepted", newTicketId: "t9" }));
    render(<TransferAcceptView transferId="x1" />);

    expect(await screen.findByText("Trần Bình chuyển cho bạn vé này.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Nhận vé" }));

    expect(await screen.findByRole("link", { name: "Xem vé" })).toHaveAttribute("href", "/tickets/t9");
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Vé đã là của bạn");
  });

  it("tells another account to sign in with the right email", async () => {
    api.getTransfer.mockRejectedValue(new ApiError(404, { code: "transfer.not_found", title: "x" }));
    render(<TransferAcceptView transferId="x1" />);

    expect(await screen.findByRole("heading", { name: "Không tìm thấy lời chuyển vé" })).toBeInTheDocument();
  });

  it("shows a closed transfer without the accept button", async () => {
    api.getTransfer.mockResolvedValue(invite({ status: "Cancelled" }));
    render(<TransferAcceptView transferId="x1" />);

    expect(await screen.findByRole("heading", { name: "Lời chuyển vé không còn hiệu lực" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Nhận vé" })).not.toBeInTheDocument();
  });
});
