import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { StrictMode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession, setSession } from "@/lib/api/session";

const offline = vi.hoisted(() => ({ forgetOfflineTickets: vi.fn(async () => undefined) }));
vi.mock("@/lib/tickets/register-sw", () => offline);

const { AuthProvider, useAuth } = await import("./auth-provider");

const user = {
  id: "0193",
  email: "an@example.com",
  fullName: "Nguyễn An",
  phoneNumber: null,
  avatarUrl: null,
  emailConfirmed: true,
  roles: ["Customer"],
};
const sessionExpiringIn = (ms: number, accessToken = "jwt") => ({
  accessToken,
  expiresAt: new Date(Date.now() + ms).toISOString(),
  user,
});
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

function Probe() {
  const { status, offline: unreachable, user: current, signOut } = useAuth();
  return (
    <div>
      <span data-testid="status">{status}</span>
      <span data-testid="offline">{String(unreachable)}</span>
      <span data-testid="name">{current?.fullName ?? ""}</span>
      <button type="button" onClick={() => void signOut()}>
        out
      </button>
    </div>
  );
}

const renderProvider = () =>
  render(
    <StrictMode>
      <AuthProvider>
        <Probe />
      </AuthProvider>
    </StrictMode>,
  );

const fetchMock = vi.fn<typeof fetch>();

beforeEach(() => {
  offline.forgetOfflineTickets.mockClear();
  vi.stubGlobal("fetch", fetchMock);
  window.localStorage.clear();
  window.sessionStorage.clear();
});

afterEach(() => {
  act(() => clearSession());
  fetchMock.mockReset();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  document.cookie = "tr_session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
});

describe("AuthProvider", () => {
  it("reports loading, not anonymous, until the session hint can be read (server render and hydration)", async () => {
    // Hydration renders with the server snapshot; a guard that saw "anonymous" there would redirect a signed-in
    // user to login before the refresh even started.
    const { renderToString } = await import("react-dom/server");
    document.cookie = "tr_session=1; path=/";

    const html = renderToString(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    );

    document.cookie = "tr_session=; path=/; max-age=0";
    expect(html).toContain(">loading<");
  });

  it("stays anonymous without calling the api when there is no session hint cookie", async () => {
    renderProvider();

    expect(screen.getByTestId("status").textContent).toBe("anonymous");
    await Promise.resolve();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("restores the session with exactly one refresh on load, even under StrictMode", async () => {
    document.cookie = "tr_session=1; path=/";
    fetchMock.mockResolvedValue(json(sessionExpiringIn(600_000)));

    renderProvider();

    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("authenticated"));
    expect(screen.getByTestId("name").textContent).toBe("Nguyễn An");
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0][0])).toBe("/api/v1/auth/refresh");
  });

  it("ends up anonymous when the refresh cookie is no longer valid", async () => {
    document.cookie = "tr_session=1; path=/";
    fetchMock.mockResolvedValue(json({ code: "auth.refresh_invalid" }, 401));

    renderProvider();

    expect(screen.getByTestId("status").textContent).toBe("loading");
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("anonymous"));
  });

  it("an unreachable api on load leaves the visitor offline, not signed out", async () => {
    document.cookie = "tr_session=1; path=/";
    fetchMock.mockRejectedValue(new TypeError("Failed to fetch"));

    renderProvider();

    await waitFor(() => expect(screen.getByTestId("offline").textContent).toBe("true"));
    expect(screen.getByTestId("status").textContent).toBe("anonymous");
  });

  it("is not offline once the api answers, even with a refusal", async () => {
    document.cookie = "tr_session=1; path=/";
    fetchMock.mockResolvedValue(json({ code: "auth.refresh_invalid" }, 401));

    renderProvider();

    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("anonymous"));
    expect(screen.getByTestId("offline").textContent).toBe("false");
  });

  it("refreshes 60 seconds before the access token expires", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
    act(() => setSession(sessionExpiringIn(10 * 60_000, "first")));
    fetchMock.mockResolvedValue(json(sessionExpiringIn(10 * 60_000, "second")));
    renderProvider();

    await act(() => vi.advanceTimersByTimeAsync(9 * 60_000 - 1_000));
    expect(fetchMock).not.toHaveBeenCalled();

    await act(() => vi.advanceTimersByTimeAsync(1_000));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0][0])).toBe("/api/v1/auth/refresh");
  });

  it("signs out through the api and forgets the user", async () => {
    act(() => setSession(sessionExpiringIn(600_000)));
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }));
    renderProvider();

    await userEvent.click(screen.getByRole("button", { name: "out" }));

    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("anonymous"));
    expect(String(fetchMock.mock.calls[0][0])).toBe("/api/v1/auth/logout");
  });

  it("Login_page_user_change_wipes_the_previous_users_offline_tickets", async () => {
    // A's tickets were stored offline on this device; B signs in (the login page has no query cache to watch).
    window.localStorage.setItem("tr.wallet.owner", "user-a");
    offline.forgetOfflineTickets.mockClear();
    renderProvider();

    act(() => setSession(sessionExpiringIn(600_000)));

    await waitFor(() => expect(offline.forgetOfflineTickets).toHaveBeenCalledTimes(1));
  });

  it("keeps the offline tickets of the same user, and loads no cleanup when nothing was stored", async () => {
    offline.forgetOfflineTickets.mockClear();
    window.localStorage.setItem("tr.wallet.owner", user.id);
    const view = renderProvider();
    act(() => setSession(sessionExpiringIn(600_000)));
    view.unmount();
    act(() => clearSession());

    window.localStorage.removeItem("tr.wallet.owner");
    renderProvider();
    act(() => setSession(sessionExpiringIn(600_000)));

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(offline.forgetOfflineTickets).not.toHaveBeenCalled();
  });

  it("Logout_clears_the_offline_tickets: keys, ticket details and the service worker", async () => {
    act(() => setSession(sessionExpiringIn(600_000)));
    fetchMock.mockRejectedValue(new TypeError("Failed to fetch"));
    renderProvider();

    // Even offline: signing out locally must not leave the tickets behind.
    await userEvent.click(screen.getByRole("button", { name: "out" }));

    await waitFor(() => expect(offline.forgetOfflineTickets).toHaveBeenCalledTimes(1));
    expect(screen.getByTestId("status").textContent).toBe("anonymous");
  });

  it("never writes the access token to localStorage or sessionStorage", async () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem");
    document.cookie = "tr_session=1; path=/";
    fetchMock.mockResolvedValue(json(sessionExpiringIn(600_000, "secret-jwt")));

    renderProvider();
    await waitFor(() => expect(screen.getByTestId("status").textContent).toBe("authenticated"));

    expect(setItem).not.toHaveBeenCalled();
    expect(JSON.stringify({ ...window.localStorage, ...window.sessionStorage })).not.toContain("secret-jwt");
    setItem.mockRestore();
  });
});
