import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { notificationKeys } from "./keys";

const api = vi.hoisted(() => ({ listNotifications: vi.fn(), markNotificationRead: vi.fn(), markAllNotificationsRead: vi.fn() }));
vi.mock("./api", () => api);
vi.mock("sonner", () => ({ toast: { error: vi.fn() } }));

const { useMarkAllRead, useMarkRead } = await import("./use-notifications");

function setup() {
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  const invalidate = vi.spyOn(client, "invalidateQueries");
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  return { client, invalidate, wrapper };
}

beforeEach(() => vi.resetAllMocks());

describe("the read mutations", () => {
  // A push or a refetch can land between the optimistic change and the api's answer; refetching once the answer is in makes the
  // inbox converge on the server's state either way.
  it.each([
    ["mark one read", () => useMarkRead(), (m: { mutateAsync: (v?: unknown) => Promise<unknown> }) => m.mutateAsync("n1"), api.markNotificationRead],
    ["mark all read", () => useMarkAllRead(), (m: { mutateAsync: (v?: unknown) => Promise<unknown> }) => m.mutateAsync(), api.markAllNotificationsRead],
  ] as const)("%s refetches the inbox once it has settled, after a success too", async (_name, hook, run, call) => {
    call.mockResolvedValue(undefined);
    const { invalidate, wrapper } = setup();
    const { result } = renderHook(hook, { wrapper });

    await act(() => run(result.current as never));

    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: notificationKeys.list }));
  });

  it("still refetches (once the error is shown) when the api refuses", async () => {
    api.markNotificationRead.mockRejectedValue(new Error("503"));
    const { invalidate, wrapper } = setup();
    const { result } = renderHook(() => useMarkRead(), { wrapper });

    await act(() => result.current.mutateAsync("n1").catch(() => undefined));

    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: notificationKeys.list }));
  });
});
