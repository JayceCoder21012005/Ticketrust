import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession } from "@/lib/api/session";
import { calls, json, renderWithProviders, resetApi, routes, signIn } from "../test-utils";
import { EventsTable } from "./events-table";

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn() }) }));

const events = [
  { id: "e1", slug: "a", title: "Sơn Tùng Live", status: "Published", startsAt: "2026-12-06T12:30:00Z", venueName: "Mỹ Đình", seatCount: 600, coverImageUrl: null },
  { id: "e2", slug: "b", title: "Hội thảo", status: "Draft", startsAt: "2026-12-10T02:00:00Z", venueName: "Nhà hát Lớn", seatCount: 0, coverImageUrl: null },
];

beforeEach(() => {
  resetApi();
  routes.set("GET /api/v1/organizer/events", () => json(events));
  signIn();
});
afterEach(() => act(() => clearSession()));

const listCalls = () => calls.filter((r) => new URL(r.url).pathname === "/api/v1/organizer/events");

describe("EventsTable", () => {
  it("lists events with Vietnamese status badges and links to the wizard", async () => {
    renderWithProviders(<EventsTable />);

    expect(await screen.findByRole("link", { name: "Sơn Tùng Live" })).toHaveAttribute("href", "/organizer/events/e1/info");
    const table = within(screen.getByRole("table"));
    expect(table.getByText("Đã xuất bản")).toBeInTheDocument();
    expect(table.getByText("Nháp")).toBeInTheDocument();
    expect(table.getByText("600")).toBeInTheDocument();
  });

  it("searches after the user stops typing for 300 ms", async () => {
    const user = userEvent.setup();
    renderWithProviders(<EventsTable />);
    await screen.findByText("Sơn Tùng Live");

    await user.type(screen.getByRole("searchbox", { name: "Tìm sự kiện" }), "son tung");

    await waitFor(() => expect(listCalls().some((r) => new URL(r.url).searchParams.get("q") === "son tung")).toBe(true));
    expect(listCalls().filter((r) => new URL(r.url).searchParams.has("q"))).toHaveLength(1);
  });

  it("filters by status", async () => {
    const user = userEvent.setup();
    renderWithProviders(<EventsTable />);
    await screen.findByText("Sơn Tùng Live");

    await user.click(screen.getByRole("button", { name: "Nháp" }));

    await waitFor(() => expect(listCalls().some((r) => new URL(r.url).searchParams.get("status") === "Draft")).toBe(true));
  });
});
