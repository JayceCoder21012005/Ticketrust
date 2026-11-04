import { act, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession } from "@/lib/api/session";
import { json, renderWithProviders, resetApi, routes, signIn } from "../../test-utils";
import { eventFixture } from "../steps.fixtures";
import { ReviewStep } from "./review-step";

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn(), refresh: vi.fn() }) }));

beforeEach(() => {
  resetApi();
  signIn();
  routes.set("GET /api/v1/organizer/venues", () => json([]));
});
afterEach(() => act(() => clearSession()));

describe("ReviewStep", () => {
  it("links each failing rule to the step that fixes it and keeps publish disabled", async () => {
    routes.set("GET /api/v1/organizer/events/e1/checklist", () =>
      json({ ready: false, failures: [{ code: "publish.missing_cover", step: "info", message: "Add a cover image." }] }),
    );
    renderWithProviders(<ReviewStep event={eventFixture()} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    expect(await screen.findByText("Có ảnh cover")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Sửa: Có ảnh cover" })).toHaveAttribute("href", "/organizer/events/e1/info");
    expect(screen.getByRole("button", { name: "Xuất bản" })).toBeDisabled();
    expect(screen.getByText(/Giá vé, sức chứa, sơ đồ ghế và các đợt mở bán sẽ bị khoá/)).toBeInTheDocument();
  });

  it("enables publish when every rule passes and shows the buyer preview", async () => {
    routes.set("GET /api/v1/organizer/events/e1/checklist", () => json({ ready: true, failures: [] }));
    renderWithProviders(<ReviewStep event={eventFixture()} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    await waitFor(() => expect(screen.getByRole("button", { name: "Xuất bản" })).toBeEnabled());
    expect(screen.getByTitle("Xem trước trang sự kiện")).toHaveAttribute("src", "/organizer/events/e1/preview");
    expect(screen.getByRole("radio", { name: "Điện thoại" })).toBeInTheDocument();
  });

  it("links a phase closing after the event start to the phases step", async () => {
    routes.set("GET /api/v1/organizer/events/e1/checklist", () =>
      json({ ready: false, failures: [{ code: "phase.after_event_start", step: "phases", message: "Sale phases must close before the event starts." }] }),
    );
    renderWithProviders(<ReviewStep event={eventFixture()} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    expect(await screen.findByRole("link", { name: "Sửa: Các đợt mở bán kết thúc trước giờ diễn" })).toHaveAttribute("href", "/organizer/events/e1/phases");
  });

  it("doesn't show a published event's checklist as failing, even when a rule stopped holding", async () => {
    routes.set("GET /api/v1/organizer/events/e1/checklist", () =>
      json({ ready: false, failures: [{ code: "publish.first_phase_too_soon", step: "phases", message: "" }] }),
    );
    renderWithProviders(<ReviewStep event={eventFixture({ status: "Published" })} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    expect(await screen.findByText("Đợt đầu mở sau ít nhất 10 phút")).toBeInTheDocument();
    expect(screen.queryByRole("img", { name: "Chưa đạt" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /^Sửa:/ })).not.toBeInTheDocument();
  });

  it("shows a published event as published, without the publish button", async () => {
    routes.set("GET /api/v1/organizer/events/e1/checklist", () => json({ ready: true, failures: [] }));
    renderWithProviders(<ReviewStep event={eventFixture({ status: "Published" })} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    expect(await screen.findByRole("link", { name: "Xem trang công khai" })).toHaveAttribute("href", "/events/dem-nhac");
    expect(screen.queryByRole("button", { name: "Xuất bản" })).not.toBeInTheDocument();
  });
});
