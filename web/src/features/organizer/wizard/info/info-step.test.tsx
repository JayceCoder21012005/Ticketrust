import { act, fireEvent, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession } from "@/lib/api/session";
import { json, renderWithProviders, resetApi, routes, signIn } from "../../test-utils";
import { eventFixture } from "../steps.fixtures";
import { InfoStep } from "./info-step";

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn() }) }));

beforeEach(() => {
  resetApi();
  routes.set("GET /api/v1/organizer/venues", () =>
    json([{ id: "v1", name: "Nhà hát Lớn", address: "1 Tràng Tiền", city: "Hà Nội", mapUrl: null, capacity: 600, shared: true }]),
  );
  signIn();
});
afterEach(() => act(() => clearSession()));

describe("InfoStep", () => {
  it("locks category, venue and dates of a published event and says why", async () => {
    renderWithProviders(<InfoStep event={eventFixture({ status: "Published" })} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    expect(screen.getByLabelText("Danh mục")).toBeDisabled();
    expect(screen.getByLabelText("Địa điểm")).toBeDisabled();
    expect(screen.getByLabelText("Ngày bắt đầu")).toBeDisabled();
    expect(screen.getByLabelText("Tên sự kiện")).toBeEnabled();
    expect(screen.getAllByLabelText("Đã khoá sau khi xuất bản").length).toBeGreaterThan(0);
  });

  it("keeps every field editable on a draft", () => {
    renderWithProviders(<InfoStep event={eventFixture()} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    expect(screen.getByLabelText("Danh mục")).toBeEnabled();
    expect(screen.getByLabelText("Ngày bắt đầu")).toBeEnabled();
  });

  it("previews the description as the plain paragraphs buyers will see", async () => {
    const user = userEvent.setup();
    renderWithProviders(<InfoStep event={eventFixture({ description: "Đoạn một.\n\nĐoạn hai." })} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    await user.click(screen.getByRole("tab", { name: "Xem trước" }));

    const preview = screen.getByTestId("description-preview");
    expect(preview.querySelectorAll("p")).toHaveLength(2);
    expect(preview).toHaveTextContent("Đoạn hai.");
  });

  it("won't save a start earlier than the close of a sale phase and says why", () => {
    const phase = { id: "p1", name: "Mở bán", opensAt: "2026-11-20T03:00:00Z", closesAt: "2026-11-30T03:00:00Z", queueEnabled: true, maxTicketsPerUser: 4, hasAccessCode: false };
    const onSaveState = vi.fn();
    renderWithProviders(<InfoStep event={eventFixture({ phases: [phase] })} onSaved={vi.fn()} onSaveState={onSaveState} />);

    fireEvent.change(screen.getByLabelText("Ngày bắt đầu"), { target: { value: "2026-11-25" } });

    expect(screen.getByText("Sự kiện phải bắt đầu sau khi đợt mở bán cuối kết thúc (10:00, T2 30/11/2026 (GMT+7)).")).toBeInTheDocument();
    expect(onSaveState).toHaveBeenLastCalledWith(expect.objectContaining({ state: "invalid" }), expect.any(Number));
  });

  it("shows the Vietnam time of the event start", () => {
    renderWithProviders(<InfoStep event={eventFixture()} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    expect(screen.getByLabelText("Ngày bắt đầu")).toHaveValue("2026-12-06");
    expect(screen.getByLabelText("Giờ bắt đầu")).toHaveValue("19:30");
  });
});
