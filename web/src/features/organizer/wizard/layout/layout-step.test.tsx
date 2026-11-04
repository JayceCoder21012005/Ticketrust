import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession } from "@/lib/api/session";
import { renderWithProviders, resetApi, signIn } from "../../test-utils";
import { eventFixture } from "../steps.fixtures";
import { LayoutStep } from "./layout-step";

const tiers = [
  { id: "t1", name: "Ghế VIP", price: 1, kind: "Seated" as const, capacity: null, color: "#7c3aed" },
  { id: "t2", name: "Khu đứng", price: 1, kind: "GeneralAdmission" as const, capacity: 300, color: "#0f766e" },
];

const layout = {
  sections: [
    { code: "A", tierId: "t1", kind: "seated" as const, rows: 20, seatsPerRow: 30, origin: { x: 100, y: 80 }, curve: 0 },
    { code: "GA", tierId: "t2", kind: "standing" as const, origin: { x: 380, y: 500 }, curve: 0 },
  ],
};

const scrollIntoView = vi.fn();

beforeEach(() => {
  resetApi();
  signIn();
  scrollIntoView.mockClear();
  Element.prototype.scrollIntoView = scrollIntoView;
});
afterEach(() => act(() => clearSession()));

describe("LayoutStep", () => {
  it("previews the saved layout and totals seats per tier", () => {
    const { container } = renderWithProviders(<LayoutStep event={eventFixture({ tiers, layout })} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    expect(container.querySelectorAll("svg circle")).toHaveLength(600);
    const totals = screen.getByRole("list", { name: "Số chỗ theo hạng vé" });
    expect(within(totals).getByText(/Ghế VIP/)).toHaveTextContent("600 ghế");
    expect(within(totals).getByText(/Khu đứng/)).toHaveTextContent("300 chỗ đứng");
  });

  it("clicking a section scrolls its card into view and marks it active", async () => {
    const user = userEvent.setup();
    const { container } = renderWithProviders(<LayoutStep event={eventFixture({ tiers, layout })} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    await user.click(container.querySelectorAll("svg circle")[10]);

    expect(scrollIntoView).toHaveBeenCalled();
    expect(screen.getByRole("group", { name: "Khu A" })).toHaveAttribute("data-active", "true");
  });

  it("shows a section's errors next to it and keeps the preview of the others", async () => {
    const user = userEvent.setup();
    const { container } = renderWithProviders(<LayoutStep event={eventFixture({ tiers, layout })} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    const rows = within(screen.getByRole("group", { name: "Khu A" })).getByLabelText("Số hàng");
    await user.clear(rows);
    await user.type(rows, "0");

    expect(within(screen.getByRole("group", { name: "Khu A" })).getByText("Số hàng từ 1 đến 100.")).toBeInTheDocument();
    expect(container.querySelectorAll("svg circle")).toHaveLength(0);
    expect(screen.getByText("GA")).toBeInTheDocument();
  });

  it("asks for a tier first when the event has none", () => {
    renderWithProviders(<LayoutStep event={eventFixture()} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    expect(screen.getByRole("link", { name: "Tạo hạng vé" })).toHaveAttribute("href", "/organizer/events/e1/tiers");
  });

  it("fills the form from a preset", async () => {
    const user = userEvent.setup();
    renderWithProviders(<LayoutStep event={eventFixture({ tiers })} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    await user.selectOptions(screen.getByLabelText("Bắt đầu từ mẫu"), "theater-3");
    await user.click(screen.getByRole("button", { name: "Dùng mẫu" }));

    expect(screen.getAllByRole("group", { name: /^Khu / })).toHaveLength(3);
  });
});
