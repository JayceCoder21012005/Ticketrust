import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession } from "@/lib/api/session";
import { renderWithProviders, resetApi, signIn } from "../../test-utils";
import { eventFixture } from "../steps.fixtures";
import { TiersStep } from "./tiers-step";

const seated = { id: "t1", name: "Ghế VIP", price: 1_500_000, kind: "Seated" as const, capacity: null, color: "#7c3aed" };
const standing = { id: "t2", name: "Khu đứng", price: 500_000, kind: "GeneralAdmission" as const, capacity: 300, color: "#0f766e" };

beforeEach(() => {
  resetApi();
  signIn();
});
afterEach(() => act(() => clearSession()));

describe("TiersStep", () => {
  it("shows the seat count of seated tiers from the layout instead of a capacity input", () => {
    renderWithProviders(
      <TiersStep event={eventFixture({ tiers: [seated, standing], seatsPerTier: { t1: 240 } })} onSaved={vi.fn()} onSaveState={vi.fn()} />,
    );

    const rows = screen.getAllByRole("row").slice(1);
    expect(within(rows[0]).getByText("Số ghế: 240 (từ sơ đồ)")).toBeInTheDocument();
    expect(within(rows[0]).queryByLabelText(/Sức chứa/)).not.toBeInTheDocument();
    expect(within(rows[1]).getByLabelText("Sức chứa hạng Khu đứng")).toHaveValue("300");
  });

  it("won't remove a tier the layout still uses and names its sections", async () => {
    const user = userEvent.setup();
    const layout = {
      sections: [
        { code: "A", tierId: "t1", kind: "seated" as const, rows: 2, seatsPerRow: 3, origin: { x: 0, y: 0 }, curve: 0 },
        { code: "B", tierId: "t1", kind: "seated" as const, rows: 2, seatsPerRow: 3, origin: { x: 200, y: 0 }, curve: 0 },
      ],
    };
    renderWithProviders(<TiersStep event={eventFixture({ tiers: [seated, standing], layout })} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    await user.click(screen.getByRole("button", { name: "Xoá hạng Ghế VIP" }));

    expect(screen.getByRole("alert")).toHaveTextContent("Hạng “Ghế VIP” đang được dùng ở khu A, B");
    expect(screen.getAllByRole("row")).toHaveLength(3);
  });

  it("won't change the kind of a tier the layout uses and says why", () => {
    const layout = { sections: [{ code: "A", tierId: "t1", kind: "seated" as const, rows: 2, seatsPerRow: 3, origin: { x: 0, y: 0 }, curve: 0 }] };
    renderWithProviders(<TiersStep event={eventFixture({ tiers: [seated, standing], layout })} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    const kind = screen.getByLabelText("Loại hạng Ghế VIP");
    expect(kind).toBeDisabled();
    expect(kind).toHaveAccessibleDescription("Đang dùng ở khu A: đổi hạng của khu ở bước Sơ đồ trước.");
    expect(screen.getByLabelText("Loại hạng Khu đứng")).toBeEnabled();
  });

  it("is read-only after publishing, behind the lock banner", () => {
    renderWithProviders(<TiersStep event={eventFixture({ status: "Published", tiers: [standing] })} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    expect(screen.getByText(/Đã khoá sau khi xuất bản/)).toBeInTheDocument();
    expect(screen.getByLabelText("Tên hạng Khu đứng")).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Thêm hạng vé" })).not.toBeInTheDocument();
  });
});
