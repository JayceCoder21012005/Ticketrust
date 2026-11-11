import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearSession, loginRedirectUrl, setSession, type Session } from "@/lib/api/session";
import { ApiError } from "@/lib/errors";

const api = vi.hoisted(() => ({ getFollowing: vi.fn(), setFollowing: vi.fn() }));
vi.mock("@/lib/follow/api", () => api);

const { FollowButton } = await import("./follow-button");
const { resetPublicClient } = await import("./follow-control");

const session: Session = {
  accessToken: "tok",
  expiresAt: new Date(Date.now() + 600_000).toISOString(),
  user: { id: "u1", email: "an@example.com", fullName: "An", phoneNumber: null, avatarUrl: null, emailConfirmed: true, roles: ["Customer"] },
};

const PAGE = "/events/son-tung-mtp-live";
const signOutCookie = () => (document.cookie = "tr_session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/");

function renderButton(client = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
  return { client, ...render(<QueryClientProvider client={client}><FollowButton eventId="e1" returnTo={PAGE} /></QueryClientProvider>) };
}

const remind = () => screen.findByRole("button", { name: "Nhắc tôi" });
const following = () => screen.findByRole("button", { name: "Đã bật nhắc nhở" });

/** The button once its state is known: while loading it has the same name, but is disabled. */
async function ready(name: string) {
  // Looked up again on every try: the lazy control replaces the placeholder button with its own.
  await waitFor(() => expect(screen.getByRole("button", { name })).toBeEnabled());
  return screen.getByRole("button", { name });
}

/** A request the test settles by hand, to look at the page while it is out. */
function deferred<T = void>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

beforeEach(() => {
  vi.resetAllMocks();
  resetPublicClient();
  document.cookie = "tr_session=1; path=/";
  setSession(session);
});

afterEach(() => {
  signOutCookie();
  act(() => clearSession());
});

describe("FollowButton signed in", () => {
  it("Follow_toggles_optimistically", async () => {
    const user = userEvent.setup();
    const request = deferred();
    api.getFollowing.mockResolvedValue(false);
    api.setFollowing.mockReturnValue(request.promise);
    renderButton();

    await user.click(await ready("Nhắc tôi"));

    // The api has not answered yet: the page already shows the new state.
    const on = await following();
    expect(on).toHaveAttribute("aria-pressed", "true");
    expect(api.setFollowing).toHaveBeenCalledWith("e1", true);

    request.resolve();
    await waitFor(() => expect(api.setFollowing).toHaveBeenCalledTimes(1));
    expect(await following()).toBeInTheDocument();

    // And back.
    api.setFollowing.mockResolvedValue(undefined);
    await user.click(on);
    expect(await remind()).toHaveAttribute("aria-pressed", "false");
    expect(api.setFollowing).toHaveBeenLastCalledWith("e1", false);
  });

  // Public pages have no Toaster (it lives in AppProviders, which would cost the public bundle), so the "toast" is an
  // inline role="alert" message right under the button.
  it("Follow_rolls_back_and_toasts_on_failure", async () => {
    const user = userEvent.setup();
    api.getFollowing.mockResolvedValue(false);
    api.setFollowing.mockRejectedValue(new ApiError(503, { code: "system.busy" }));
    renderButton();

    await user.click(await ready("Nhắc tôi"));

    expect(await screen.findByRole("alert")).toHaveTextContent("Hệ thống đang bận. Vui lòng thử lại sau giây lát.");
    const back = await remind();
    expect(back).toHaveAttribute("aria-pressed", "false");
    expect(back).toBeEnabled();

    // Trying again clears the message.
    api.setFollowing.mockResolvedValue(undefined);
    await user.click(back);
    expect(await following()).toBeInTheDocument();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("rolls an unfollow back to following when the api refuses", async () => {
    const user = userEvent.setup();
    api.getFollowing.mockResolvedValue(true);
    api.setFollowing.mockRejectedValue(new Error("offline"));
    renderButton();

    await user.click(await ready("Đã bật nhắc nhở"));

    expect(await screen.findByRole("alert")).toHaveTextContent("Không kết nối được máy chủ");
    expect(await following()).toHaveAttribute("aria-pressed", "true");
  });

  it("Button_label_switches_between_Nhac_toi_and_Da_bat_nhac_nho", async () => {
    api.getFollowing.mockResolvedValue(true);
    const { unmount } = renderButton();
    expect(await following()).toHaveAttribute("aria-pressed", "true");
    expect(screen.queryByRole("button", { name: "Nhắc tôi" })).toBeNull();
    unmount();

    api.getFollowing.mockResolvedValue(false);
    renderButton(new QueryClient());
    expect(await remind()).toHaveAttribute("aria-pressed", "false");
    expect(screen.queryByRole("button", { name: "Đã bật nhắc nhở" })).toBeNull();
  });

  it("is a disabled, busy button while the state is loading", async () => {
    const load = deferred<boolean>();
    api.getFollowing.mockReturnValue(load.promise);
    renderButton();

    const button = await remind();
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute("aria-busy", "true");
    expect(api.setFollowing).not.toHaveBeenCalled();

    load.resolve(false);
    await waitFor(() => expect(button).toBeEnabled());
    expect(button).not.toHaveAttribute("aria-busy", "true");
  });

  it("offers a retry when the state cannot be loaded", async () => {
    const user = userEvent.setup();
    api.getFollowing.mockRejectedValueOnce(new Error("boom"));
    renderButton();

    expect(await screen.findByText("Không tải được trạng thái nhắc nhở.")).toBeInTheDocument();
    expect(await remind()).toBeDisabled();
    api.getFollowing.mockResolvedValue(true);
    await user.click(screen.getByRole("button", { name: "Thử lại" }));

    expect(await following()).toBeEnabled();
  });

  it("sends one request for a double click while the first is pending", async () => {
    const user = userEvent.setup();
    const request = deferred();
    api.getFollowing.mockResolvedValue(false);
    api.setFollowing.mockReturnValue(request.promise);
    renderButton();

    await user.dblClick(await ready("Nhắc tôi"));

    expect(api.setFollowing).toHaveBeenCalledTimes(1);
    expect(await following()).toBeInTheDocument();
    request.resolve();
  });

  it("a 409 event.not_followable says so, rolls back and disables the button", async () => {
    const user = userEvent.setup();
    api.getFollowing.mockResolvedValue(false);
    api.setFollowing.mockRejectedValue(new ApiError(409, { code: "event.not_followable" }));
    renderButton();

    await user.click(await ready("Nhắc tôi"));

    expect(await screen.findByRole("alert")).toHaveTextContent("Sự kiện này không còn nhận nhắc nhở nữa.");
    const button = await remind();
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute("aria-pressed", "false");

    await user.click(button);
    expect(api.setFollowing).toHaveBeenCalledTimes(1);
  });

  it("is operable by keyboard", async () => {
    const user = userEvent.setup();
    api.getFollowing.mockResolvedValue(false);
    api.setFollowing.mockResolvedValue(undefined);
    renderButton();

    const button = await ready("Nhắc tôi");
    await user.tab();
    expect(button).toHaveFocus();
    await user.keyboard(" ");
    expect(await following()).toHaveFocus();
    await user.keyboard("{Enter}");
    expect(await remind()).toHaveFocus();
    expect(api.setFollowing).toHaveBeenNthCalledWith(1, "e1", true);
    expect(api.setFollowing).toHaveBeenNthCalledWith(2, "e1", false);
  });

  it("two buttons for one event share one request and one state", async () => {
    const user = userEvent.setup();
    api.getFollowing.mockResolvedValue(false);
    api.setFollowing.mockResolvedValue(undefined);
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <FollowButton eventId="e1" returnTo={PAGE} />
        <FollowButton eventId="e1" returnTo={PAGE} />
      </QueryClientProvider>,
    );

    const [first] = await screen.findAllByRole("button", { name: "Nhắc tôi" });
    await waitFor(() => expect(first).toBeEnabled());
    await user.click(first!);

    expect(await screen.findAllByRole("button", { name: "Đã bật nhắc nhở" })).toHaveLength(2);
    expect(api.getFollowing).toHaveBeenCalledTimes(1);
    expect(client.getQueryData(["follow", "e1"])).toMatchObject({ following: true });
  });

  it("on a public page (no query client above) the state is dropped when another user signs in", async () => {
    api.getFollowing.mockResolvedValueOnce(true).mockResolvedValue(false);
    render(<FollowButton eventId="e1" returnTo={PAGE} />);
    expect(await following()).toBeInTheDocument();

    act(() => setSession({ ...session, user: { ...session.user, id: "u2" } }));

    // Not user 1's "following": the next user's own state is asked for.
    expect(await screen.findByRole("button", { name: "Nhắc tôi" })).toBeInTheDocument();
    expect(api.getFollowing).toHaveBeenCalledTimes(2);
  });

  it("does not ask the api for a state without a live session (stale hint)", async () => {
    act(() => clearSession());
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 401 })));
    renderButton();

    expect(await screen.findByRole("link", { name: "Nhắc tôi" })).toHaveAttribute("href", loginRedirectUrl(PAGE));
    expect(api.getFollowing).not.toHaveBeenCalled();
    vi.unstubAllGlobals();
  });
});

describe("FollowButton anonymous", () => {
  beforeEach(() => {
    signOutCookie();
    act(() => clearSession());
  });

  it("Anonymous_click_redirects_to_login", () => {
      renderButton();

    const link = screen.getByRole("link", { name: "Nhắc tôi" });

    expect(link).toHaveAttribute("href", "/login?returnUrl=%2Fevents%2Fson-tung-mtp-live");
    expect(link).toHaveAttribute("href", loginRedirectUrl(PAGE));
    expect(screen.queryByRole("button")).toBeNull();
    expect(api.getFollowing).not.toHaveBeenCalled();
  });

  it("needs no query client and makes no request for an anonymous visitor", () => {
    // No QueryClientProvider here: the island must not need one (nor create one) until a session hint exists.
    render(<FollowButton eventId="e1" returnTo={PAGE} />);

    expect(screen.getByRole("link", { name: "Nhắc tôi" })).toBeInTheDocument();
    expect(api.getFollowing).not.toHaveBeenCalled();
  });
});
