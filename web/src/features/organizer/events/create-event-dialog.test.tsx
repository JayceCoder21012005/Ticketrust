import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession } from "@/lib/api/session";
import { callsTo, json, renderWithProviders, resetApi, routes, signIn } from "../test-utils";
import { CreateEventDialog } from "./create-event-dialog";

const push = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ push, replace: vi.fn() }) }));

const venues = [
  { id: "v1", name: "Nhà hát Lớn", address: "1 Tràng Tiền", city: "Hà Nội", mapUrl: null, capacity: 600, shared: true },
];

beforeEach(() => {
  resetApi();
  push.mockClear();
  routes.set("GET /api/v1/organizer/venues", () => json(venues));
  routes.set("POST /api/v1/organizer/events", () => json({ id: "e1", slug: "dem-nhac" }, 201));
  signIn();
});
afterEach(() => act(() => clearSession()));

async function fill(user: ReturnType<typeof userEvent.setup>, end: { date: string; time: string }) {
  await user.click(screen.getByRole("button", { name: "Tạo sự kiện" }));
  await user.type(screen.getByLabelText("Tên sự kiện"), "Đêm nhạc mùa đông");
  await user.selectOptions(screen.getByLabelText("Danh mục"), "concert");
  await waitFor(() => expect(screen.getByRole("option", { name: /Nhà hát Lớn/ })).toBeInTheDocument());
  await user.selectOptions(screen.getByLabelText("Địa điểm"), "v1");
  await user.type(screen.getByLabelText("Ngày bắt đầu"), "2026-12-06");
  await user.type(screen.getByLabelText("Giờ bắt đầu"), "19:30");
  await user.type(screen.getByLabelText("Ngày kết thúc"), end.date);
  await user.type(screen.getByLabelText("Giờ kết thúc"), end.time);
  await user.click(screen.getByRole("button", { name: "Tạo bản nháp" }));
}

// Filling eight fields with userEvent takes a few seconds on a busy machine.
describe("CreateEventDialog", { timeout: 15_000 }, () => {
  it("refuses an end before the start", async () => {
    const user = userEvent.setup();
    renderWithProviders(<CreateEventDialog />);

    await fill(user, { date: "2026-12-06", time: "18:00" });

    expect(await screen.findByText("Giờ kết thúc phải sau giờ bắt đầu")).toBeInTheDocument();
    expect(callsTo("POST", "/api/v1/organizer/events")).toHaveLength(0);
  });

  it("creates the draft in Vietnam time and opens the wizard", async () => {
    const user = userEvent.setup();
    renderWithProviders(<CreateEventDialog />);

    await fill(user, { date: "2026-12-06", time: "22:30" });

    await waitFor(() => expect(push).toHaveBeenCalledWith("/organizer/events/e1/info"));
    const body = await callsTo("POST", "/api/v1/organizer/events")[0].json();
    expect(body).toEqual({
      title: "Đêm nhạc mùa đông",
      category: "concert",
      venueId: "v1",
      startsAt: "2026-12-06T19:30:00+07:00",
      endsAt: "2026-12-06T22:30:00+07:00",
    });
  });
});
