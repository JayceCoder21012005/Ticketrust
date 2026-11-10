import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { act, render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession, setSession, type Session } from "@/lib/api/session";
import { AppProviders } from "./app-providers";

const session = (id: string, token = "t"): Session => ({
  accessToken: token,
  expiresAt: new Date(Date.now() + 600_000).toISOString(),
  user: { id, email: `${id}@example.com`, fullName: id, phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
});

/** The test reads the provider's client from here once the probe has mounted. */
const seen: { client?: QueryClient } = {};
function Probe() {
  const found = useQueryClient();
  useEffect(() => {
    seen.client = found;
  }, [found]);
  return null;
}

const cache = () => seen.client!;

function mount() {
  render(
    <AppProviders>
      <Probe />
    </AppProviders>,
  );
}

beforeEach(() => {
  // The toaster reads the colour scheme.
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: false, media: query, addEventListener: () => undefined, removeEventListener: () => undefined }));
});

afterEach(() => {
  act(() => clearSession());
  vi.unstubAllGlobals();
});

describe("AppProviders query cache", () => {
  it("is emptied when the user signs out, so the next person never sees someone else's orders", () => {
    act(() => setSession(session("a")));
    mount();
    cache().setQueryData(["my-orders"], { pages: [] });
    cache().setQueryData(["account-order", "o1"], { id: "o1" });

    act(() => clearSession());

    expect(cache().getQueryData(["my-orders"])).toBeUndefined();
    expect(cache().getQueryData(["account-order", "o1"])).toBeUndefined();
  });

  it("is emptied when another user takes over the session without a sign-out in between", () => {
    act(() => setSession(session("a")));
    mount();
    cache().setQueryData(["my-orders"], { pages: [] });

    act(() => setSession(session("b")));

    expect(cache().getQueryData(["my-orders"])).toBeUndefined();
  });

  it("keeps the cache across a token refresh or a profile change of the same user", () => {
    act(() => setSession(session("a")));
    mount();
    cache().setQueryData(["my-orders"], { pages: [] });

    act(() => setSession(session("a", "refreshed")));

    expect(cache().getQueryData(["my-orders"])).toEqual({ pages: [] });
  });

  it("keeps what was cached while the session was still being restored", () => {
    mount();
    cache().setQueryData(["public"], { x: 1 });

    act(() => setSession(session("a")));

    expect(cache().getQueryData(["public"])).toEqual({ x: 1 });
  });
});
