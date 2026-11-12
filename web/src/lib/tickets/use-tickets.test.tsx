import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ticketKeys } from "./keys";

const api = vi.hoisted(() => ({ getMyTickets: vi.fn(), getTicket: vi.fn() }));
const session = vi.hoisted(() => ({ present: true }));
vi.mock("./api", () => api);
vi.mock("@/lib/api/use-session-present", () => ({ useSessionPresent: () => session.present }));

const { useMyTickets, useTicket } = await import("./use-tickets");

function setup() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  return { client, wrapper };
}

beforeEach(() => {
  vi.resetAllMocks();
  session.present = true;
});

describe("ticket queries", () => {
  it("useMyTickets loads one scope under its key", async () => {
    api.getMyTickets.mockResolvedValue([]);
    const { client, wrapper } = setup();
    const { result } = renderHook(() => useMyTickets("past"), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(api.getMyTickets).toHaveBeenCalledWith("past", expect.anything());
    expect(client.getQueryData(ticketKeys.list("past"))).toEqual([]);
  });

  it("useTicket loads the ticket under its key", async () => {
    api.getTicket.mockResolvedValue({ id: "t1" });
    const { client, wrapper } = setup();
    const { result } = renderHook(() => useTicket("t1"), { wrapper });

    await waitFor(() => expect(result.current.data).toEqual({ id: "t1" }));
    expect(api.getTicket).toHaveBeenCalledWith("t1", expect.anything());
    expect(client.getQueryData(ticketKeys.detail("t1"))).toEqual({ id: "t1" });
  });

  it("asks for nothing until there is a session", () => {
    session.present = false;
    const { wrapper } = setup();
    renderHook(() => useMyTickets("upcoming"), { wrapper });
    renderHook(() => useTicket("t1"), { wrapper });

    expect(api.getMyTickets).not.toHaveBeenCalled();
    expect(api.getTicket).not.toHaveBeenCalled();
  });
});
