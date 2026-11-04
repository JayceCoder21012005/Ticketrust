import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession } from "@/lib/api/session";
import { callsTo, json, renderWithProviders, resetApi, routes, signIn } from "../../test-utils";
import { eventFixture } from "../steps.fixtures";
import { PhasesStep } from "./phases-step";

const event = eventFixture({ startsAt: "2027-01-10T12:00:00Z", endsAt: "2027-01-10T15:00:00Z" });

beforeEach(() => {
  resetApi();
  signIn();
  routes.set("GET /api/v1/time", () => json({ serverNow: new Date().toISOString() }));
  routes.set("PUT /api/v1/organizer/events/e1/phases", () => json({ ...event, version: "2" }));
});
afterEach(() => act(() => clearSession()));

async function setTime(user: ReturnType<typeof userEvent.setup>, card: HTMLElement, label: string, value: string) {
  const input = within(card).getByLabelText(label);
  await user.clear(input);
  await user.type(input, value);
}

describe("PhasesStep", { timeout: 20_000 }, () => {
  it("marks overlapping phases before anything is saved", async () => {
    const user = userEvent.setup();
    renderWithProviders(<PhasesStep event={event} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    await user.click(screen.getByRole("button", { name: "Thêm đợt mở bán" }));
    await user.click(screen.getByRole("button", { name: "Thêm đợt mở bán" }));
    const [first, second] = screen.getAllByRole("group", { name: /^Đợt / });
    await setTime(user, first, "Ngày mở", "2027-01-02");
    await setTime(user, first, "Giờ mở", "10:00");
    await setTime(user, first, "Ngày đóng", "2027-01-04");
    await setTime(user, first, "Giờ đóng", "10:00");
    await setTime(user, second, "Ngày mở", "2027-01-03");
    await setTime(user, second, "Giờ mở", "10:00");
    await setTime(user, second, "Ngày đóng", "2027-01-05");
    await setTime(user, second, "Giờ đóng", "10:00");

    expect(within(first).getByText("Trùng giờ với đợt khác")).toBeInTheDocument();
    expect(within(second).getByText("Trùng giờ với đợt khác")).toBeInTheDocument();
    expect(callsTo("PUT", "/api/v1/organizer/events/e1/phases")).toHaveLength(0);
  });

  it("sends Vietnam times with +07:00 and a new presale code", async () => {
    const user = userEvent.setup();
    renderWithProviders(<PhasesStep event={event} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    await user.click(screen.getByRole("button", { name: "Thêm đợt mở bán" }));
    const [card] = screen.getAllByRole("group", { name: /^Đợt / });
    await setTime(user, card, "Ngày mở", "2027-01-02");
    await setTime(user, card, "Giờ mở", "10:00");
    await setTime(user, card, "Ngày đóng", "2027-01-04");
    await setTime(user, card, "Giờ đóng", "22:00");
    await user.type(within(card).getByLabelText("Mã presale (tuỳ chọn)"), "FAN-2027");

    await waitFor(() => expect(callsTo("PUT", "/api/v1/organizer/events/e1/phases")).toHaveLength(1), { timeout: 3000 });
    const body = await callsTo("PUT", "/api/v1/organizer/events/e1/phases")[0].json();
    expect(body.phases[0]).toMatchObject({ opensAt: "2027-01-02T10:00:00+07:00", closesAt: "2027-01-04T22:00:00+07:00", accessCode: "FAN-2027" });
  });

  it("keeps an existing code unless the organizer changes or removes it", async () => {
    const user = userEvent.setup();
    const withCode = eventFixture({
      ...event,
      phases: [{ id: "p1", name: "Presale", opensAt: "2027-01-02T03:00:00Z", closesAt: "2027-01-03T03:00:00Z", queueEnabled: true, maxTicketsPerUser: 2, hasAccessCode: true }],
    });
    renderWithProviders(<PhasesStep event={withCode} onSaved={vi.fn()} onSaveState={vi.fn()} />);

    expect(screen.getByText("Đã đặt mã")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Bỏ mã" }));

    await waitFor(() => expect(callsTo("PUT", "/api/v1/organizer/events/e1/phases")).toHaveLength(1), { timeout: 3000 });
    const body = await callsTo("PUT", "/api/v1/organizer/events/e1/phases")[0].json();
    expect(body.phases[0]).toMatchObject({ id: "p1", accessCode: "" });
  });
});
