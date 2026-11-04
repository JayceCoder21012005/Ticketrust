import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render } from "@testing-library/react";
import type { ReactNode } from "react";
import { vi } from "vitest";
import { AuthProvider } from "@/components/auth/auth-provider";
import { setSession, type SessionUser } from "@/lib/api/session";

/** Test-only: a fake api at the fetch layer, like the browser would see it, plus providers and a signed-in user. */
type Route = (request: Request) => Response | Promise<Response>;

export const routes = new Map<string, Route>();
export const calls: Request[] = [];

export const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

export const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
  const request = input instanceof Request ? input : new Request(new URL(String(input), "http://localhost:3000"), init);
  calls.push(request.clone());
  const url = new URL(request.url);
  const route = routes.get(`${request.method} ${url.pathname}`);
  return route ? route(request) : json({ status: 404, code: "test.no_route" }, 404);
});

export const callsTo = (method: string, path: string) =>
  calls.filter((r) => r.method === method && new URL(r.url).pathname === path);

export function organizerUser(roles: string[] = ["Organizer"]): SessionUser {
  return { id: "u1", email: "org@example.com", fullName: "Tổ Chức", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles };
}

export function signIn(user: SessionUser = organizerUser()) {
  act(() => setSession({ accessToken: "jwt", expiresAt: new Date(Date.now() + 600_000).toISOString(), user }));
}

export function resetApi() {
  routes.clear();
  calls.length = 0;
  fetchMock.mockClear();
  vi.stubGlobal("fetch", fetchMock);
}

export function renderWithProviders(ui: ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <AuthProvider>{ui}</AuthProvider>
    </QueryClientProvider>,
  );
}
