import { act, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { TicketTier } from "@/lib/catalog/types";
import { createSeatStore } from "@/lib/seats/seat-store";
import type { SeatInfo, SeatMap as SeatMapData } from "@/lib/seats/types";
import { SeatMap } from "./seat-map";

const tiers: TicketTier[] = [
  { id: "vip", name: "VIP", price: 1_500_000, currency: "VND", kind: "Seated", color: "#e11d48" },
  { id: "std", name: "Thường", price: 600_000, currency: "VND", kind: "Seated", color: "#2563eb" },
];

const seat = (index: number, section: string, row: number, number: number, tierId: string): SeatInfo => ({
  index,
  section,
  row: String(row),
  number: String(number),
  x: 100 + number * 24 + (section === "B" ? 400 : 0),
  y: 100 + row * 28,
  tierId,
});

const map: SeatMapData = {
  eventId: "e1",
  version: "v1",
  sections: [
    { name: "A", seatCount: 4 },
    { name: "B", seatCount: 2 },
  ],
  seats: [seat(0, "A", 1, 1, "vip"), seat(1, "A", 1, 2, "vip"), seat(2, "A", 2, 1, "vip"), seat(3, "A", 2, 2, "vip"), seat(4, "B", 1, 1, "std"), seat(5, "B", 1, 2, "std")],
};

function setup(filter = new Set<string>()) {
  const store = createSeatStore(map.seats.length);
  store.load({ version: 1, unavailable: [3], mine: [] });
  const onToggle = vi.fn((index: number) => store.toggle(index));
  const view = render(<SeatMap seatMap={map} tiers={tiers} store={store} tierFilter={filter} onToggle={onToggle} />);
  return { store, onToggle, ...view };
}

const zoomInto = (name: RegExp) => fireEvent.click(screen.getByRole("button", { name }));

describe("SeatMap", () => {
  it("shows the sections first, then the seats of the section you open", () => {
    setup();

    expect(screen.getByRole("button", { name: /Khu A, còn 3 ghế trống/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /hàng 1, ghế 1/ })).not.toBeInTheDocument();

    zoomInto(/Khu A,/);

    expect(screen.getAllByRole("button", { name: /^Khu A, hàng/ })).toHaveLength(4);
    expect(screen.queryByRole("button", { name: /^Khu B, hàng/ })).not.toBeInTheDocument();
  });

  it("toggles a free seat when clicked", () => {
    const { onToggle } = setup();
    zoomInto(/Khu A,/);

    fireEvent.click(screen.getByRole("button", { name: /hàng 1, ghế 1,/ }));

    expect(onToggle).toHaveBeenCalledWith(0);
    expect(screen.getByRole("button", { name: /hàng 1, ghế 1,.*đang chọn/ })).toHaveAttribute("aria-pressed", "true");
  });

  it("marks taken seats as disabled and does not toggle them", () => {
    const { onToggle } = setup();
    zoomInto(/Khu A,/);

    const taken = screen.getByRole("button", { name: /hàng 2, ghế 2,.*đã có người chọn/ });
    fireEvent.click(taken);

    expect(taken).toHaveAttribute("aria-disabled", "true");
    expect(onToggle).not.toHaveBeenCalled();
  });

  it("updates a seat's label when a delta changes it", () => {
    const { store } = setup();
    zoomInto(/Khu A,/);

    act(() => {
      store.applyDelta({ from: 2, v: 2, held: [1], freed: [3], sold: [], tiers: [] });
    });

    expect(screen.getByRole("button", { name: /hàng 1, ghế 2,.*đã có người chọn/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /hàng 2, ghế 2,.*còn trống/ })).toBeInTheDocument();
  });

  it("dims seats of tiers outside the filter", () => {
    render(
      <SeatMap seatMap={{ ...map, sections: [{ name: "B", seatCount: 2 }], seats: map.seats.slice(4) }} tiers={tiers} store={createSeatStore(6)} tierFilter={new Set(["vip"])} onToggle={vi.fn()} />,
    );

    // A single section opens straight away.
    const seats = screen.getAllByRole("button", { name: /^Khu B, hàng/ });
    seats.forEach((s) => expect(s).toHaveAttribute("data-dimmed", "true"));
  });

  it("lets keyboard users move between seats and select with Enter", async () => {
    const user = userEvent.setup();
    const { onToggle } = setup();
    zoomInto(/Khu A,/);

    const first = screen.getByRole("button", { name: /hàng 1, ghế 1,/ });
    act(() => first.focus());
    await user.keyboard("{ArrowRight}");
    expect(screen.getByRole("button", { name: /hàng 1, ghế 2,/ })).toHaveFocus();

    await user.keyboard("{Enter}");
    expect(onToggle).toHaveBeenCalledWith(1);
  });

  it("goes back to the overview with Escape or the overview button", async () => {
    const user = userEvent.setup();
    setup();
    zoomInto(/Khu A,/);

    await user.click(screen.getByRole("button", { name: "Toàn cảnh" }));

    expect(screen.getByRole("button", { name: /Khu A, còn/ })).toBeInTheDocument();
    expect(within(document.body).queryByRole("button", { name: /^Khu A, hàng/ })).not.toBeInTheDocument();
  });

  it("moves focus to the first free seat when a section opens and back to its toggle when it closes", async () => {
    const user = userEvent.setup();
    setup();
    const toggle = screen.getByRole("button", { name: /Khu A,/ });
    toggle.focus();

    await user.keyboard("{Enter}");
    expect(screen.getByRole("button", { name: /hàng 1, ghế 1,/ })).toHaveFocus();

    await user.keyboard("{Escape}");
    expect(screen.getByRole("button", { name: /Khu A, còn/ })).toHaveFocus();

    await user.keyboard("{Enter}");
    await user.click(screen.getByRole("button", { name: "Toàn cảnh" }));
    expect(screen.getByRole("button", { name: /Khu A, còn/ })).toHaveFocus();
  });

  it("focuses the first free seat, skipping taken ones", () => {
    const store = createSeatStore(map.seats.length);
    store.load({ version: 1, unavailable: [0], mine: [] });
    render(<SeatMap seatMap={map} tiers={tiers} store={store} tierFilter={new Set()} onToggle={vi.fn()} />);

    zoomInto(/Khu A,/);

    expect(screen.getByRole("button", { name: /hàng 1, ghế 2,/ })).toHaveFocus();
  });

  it("announces opening a section and going back to the overview in a status region", async () => {
    const user = userEvent.setup();
    setup();
    expect(screen.getByRole("status")).toBeEmptyDOMElement();

    zoomInto(/Khu A,/);
    expect(screen.getByRole("status")).toHaveTextContent("Đã mở Khu A, còn 3 ghế trống");

    await user.click(screen.getByRole("button", { name: "Toàn cảnh" }));
    expect(screen.getByRole("status")).toHaveTextContent("Toàn cảnh, 2 khu");
  });

  it("does not steal focus on first render", () => {
    setup();
    expect(document.body).toHaveFocus();
  });
});
