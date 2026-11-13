import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useSyncExternalStore } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, NetworkError } from "@/lib/errors";
import type { TicketEventGroup, TicketItem } from "@/lib/tickets/types";

/** A URL the component can change: `router.replace` writes the query, `useSearchParams` re-renders on it. */
const url = vi.hoisted(() => {
  const listeners = new Set<() => void>();
  const state = { search: "" };
  return {
    state,
    listeners,
    set(next: string) {
      state.search = next;
      listeners.forEach((listener) => listener());
    },
  };
});

const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn() }));
vi.mock("next/navigation", () => ({
  useRouter: () => router,
  useSearchParams: () => {
    const search = useSyncExternalStore(
      (listener) => {
        url.listeners.add(listener);
        return () => url.listeners.delete(listener);
      },
      () => url.state.search,
      () => url.state.search,
    );
    return new URLSearchParams(search);
  },
}));

const auth = vi.hoisted(() => ({ status: "authenticated" }));
vi.mock("@/components/auth/auth-provider", () => ({ useAuth: () => ({ status: auth.status, user: { id: "u1" } }) }));

const session = vi.hoisted(() => ({ present: true }));
vi.mock("@/lib/api/use-session-present", () => ({ useSessionPresent: () => session.present }));

const api = vi.hoisted(() => ({ getMyTickets: vi.fn(), getTicket: vi.fn() }));
vi.mock("@/lib/tickets/api", () => api);

const offline = vi.hoisted(() => ({ purgeEnded: vi.fn(), claim: vi.fn(), registerTicketWorker: vi.fn() }));
vi.mock("@/lib/tickets/key-cache", () => ({ keyCache: { purgeEnded: offline.purgeEnded, claim: offline.claim } }));
vi.mock("@/lib/tickets/register-sw", () => ({ registerTicketWorker: offline.registerTicketWorker }));

const { TicketsView } = await import("./tickets-view");

const FUTURE = "2099-12-20T13:00:00Z";
const PAST = "2020-03-01T13:00:00Z";

const ticket = (n: number, extra: Partial<TicketItem> = {}): TicketItem => ({
  id: `0000000${n}-aaaa-bbbb-cccc-dddddddddddd`,
  orderId: "o1",
  tierName: "Hạng VIP",
  seat: { section: "A", row: "1", number: String(n) },
  status: "Valid",
  holderName: "Nguyễn An",
  ...extra,
});

const group = (n: number, tickets: TicketItem[], extra: Partial<TicketEventGroup> = {}): TicketEventGroup => ({
  eventId: `e${n}`,
  slug: `dem-nhac-${n}`,
  title: `Đêm nhạc ${n}`,
  coverImageUrl: `https://cdn.example.com/cover-${n}.jpg`,
  startsAt: FUTURE,
  endsAt: "2099-12-20T16:00:00Z",
  venueName: "Nhà hát Hòa Bình",
  city: "TP.HCM",
  status: "Published",
  tickets,
  ...extra,
});

const pastGroup = (n: number, tickets: TicketItem[], extra: Partial<TicketEventGroup> = {}) =>
  group(n, tickets, { startsAt: PAST, endsAt: "2020-03-01T16:00:00Z", status: "Ended", ...extra });

function renderView() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <TicketsView />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.resetAllMocks();
  auth.status = "authenticated";
  session.present = true;
  url.state.search = "";
  offline.purgeEnded.mockResolvedValue(undefined);
  offline.claim.mockResolvedValue(undefined);
  router.replace.mockImplementation((href: string) => url.set(href.includes("?") ? href.slice(href.indexOf("?")) : ""));
});

describe("TicketsView", () => {
  it("Ended_keys_are_purged_when_the_list_loads, for this user, and the wallet worker is registered", async () => {
    api.getMyTickets.mockResolvedValue([group(1, [ticket(1)])]);
    renderView();

    expect(await screen.findByRole("link", { name: /Đêm nhạc 1/ })).toBeInTheDocument();
    await waitFor(() => expect(offline.purgeEnded).toHaveBeenCalledTimes(1));
    expect(offline.claim).toHaveBeenCalledWith("u1");
    expect(Math.abs(offline.purgeEnded.mock.calls[0]![0] - Date.now())).toBeLessThan(60_000);
    expect(offline.registerTicketWorker).toHaveBeenCalled();
  });

  it("Tabs_switch_between_upcoming_and_past", async () => {
    const user = userEvent.setup();
    api.getMyTickets.mockImplementation(async (scope: string) =>
      scope === "upcoming" ? [group(1, [ticket(1)])] : [pastGroup(2, [ticket(2)])],
    );
    renderView();

    expect(await screen.findByRole("link", { name: /Đêm nhạc 1/ })).toBeInTheDocument();
    expect(api.getMyTickets).toHaveBeenCalledWith("upcoming", expect.anything());
    expect(screen.getByRole("tab", { name: "Sắp diễn ra" })).toHaveAttribute("aria-selected", "true");

    await user.click(screen.getByRole("tab", { name: "Đã qua" }));

    expect(await screen.findByRole("link", { name: /Đêm nhạc 2/ })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /Đêm nhạc 1/ })).not.toBeInTheDocument();
    expect(api.getMyTickets).toHaveBeenCalledWith("past", expect.anything());
    expect(screen.getByRole("tab", { name: "Đã qua" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tab", { name: "Sắp diễn ra" })).toHaveAttribute("aria-selected", "false");
  });

  it("Cards_show_cover_date_venue_and_ticket_count", async () => {
    api.getMyTickets.mockResolvedValue([group(1, [ticket(1), ticket(2, { tierName: "Hạng thường" }), ticket(3)])]);
    renderView();

    const card = within(await screen.findByRole("listitem"));
    expect(card.getByRole("heading", { level: 2, name: "Đêm nhạc 1" })).toBeInTheDocument();
    // 13:00Z on 20/12/2099 is 20:00 in Vietnam, whatever the device zone.
    expect(card.getByText("20:00 20/12/2099")).toBeInTheDocument();
    expect(card.getByText("Nhà hát Hòa Bình, TP.HCM")).toBeInTheDocument();
    expect(card.getByText("3 vé")).toBeInTheDocument();
    expect(card.getByText("Hạng VIP, Hạng thường")).toBeInTheDocument();
    const cover = screen.getByRole("listitem").querySelector("img");
    expect(cover).not.toBeNull();
    expect(decodeURIComponent(cover!.getAttribute("src") ?? "")).toContain("cover-1.jpg");
  });

  it("links a single ticket to its wallet page, with its seat", async () => {
    api.getMyTickets.mockResolvedValue([group(1, [ticket(7)])]);
    renderView();

    const link = await screen.findByRole("link", { name: /Đêm nhạc 1/ });
    expect(link).toHaveAttribute("href", "/tickets/00000007-aaaa-bbbb-cccc-dddddddddddd");
    const card = within(screen.getByRole("listitem"));
    expect(card.getByText("1 vé")).toBeInTheDocument();
    expect(card.getByText("Hạng VIP · Khu A, hàng 1, ghế 7")).toBeInTheDocument();
  });

  it("links a card with several tickets to the first one", async () => {
    api.getMyTickets.mockResolvedValue([group(1, [ticket(4), ticket(5), ticket(6)])]);
    renderView();

    expect(await screen.findByRole("link", { name: /Đêm nhạc 1/ })).toHaveAttribute("href", "/tickets/00000004-aaaa-bbbb-cccc-dddddddddddd");
  });

  it("shows a cancelled event with a 'Đã huỷ' chip", async () => {
    url.state.search = "?scope=past";
    api.getMyTickets.mockResolvedValue([group(1, [ticket(1)], { status: "Cancelled" })]);
    renderView();

    expect(within(await screen.findByRole("listitem")).getByText("Đã huỷ")).toBeInTheDocument();
  });

  it("shows an event that has taken place with a 'Đã diễn ra' chip, and an upcoming one with no chip", async () => {
    url.state.search = "?scope=past";
    api.getMyTickets.mockResolvedValue([pastGroup(2, [ticket(2)])]);
    renderView();
    expect(within(await screen.findByRole("listitem")).getByText("Đã diễn ra")).toBeInTheDocument();
  });

  it("shows no chip on an upcoming event", async () => {
    api.getMyTickets.mockResolvedValue([group(1, [ticket(1)])]);
    renderView();

    const card = within(await screen.findByRole("listitem"));
    expect(card.queryByText("Đã diễn ra")).not.toBeInTheDocument();
    expect(card.queryByText("Đã huỷ")).not.toBeInTheDocument();
  });

  it("uses the generated poster when the event has no cover", async () => {
    api.getMyTickets.mockResolvedValue([group(1, [ticket(1)], { coverImageUrl: null })]);
    renderView();

    const item = await screen.findByRole("listitem");
    expect(item.querySelector("img")).toBeNull();
    expect(item.querySelector("[aria-hidden='true'][style*='gradient']")).not.toBeNull();
  });

  it("Empty_state_links_to_the_catalog", async () => {
    api.getMyTickets.mockResolvedValue([]);
    renderView();

    expect(await screen.findByText("Bạn chưa có vé nào sắp diễn ra")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Khám phá sự kiện" })).toHaveAttribute("href", "/events");
    expect(screen.queryByRole("list")).not.toBeInTheDocument();
  });

  it("says there are no past tickets on the past tab", async () => {
    url.state.search = "?scope=past";
    api.getMyTickets.mockResolvedValue([]);
    renderView();

    expect(await screen.findByText("Bạn chưa có vé nào ở các sự kiện đã qua")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Khám phá sự kiện" })).toHaveAttribute("href", "/events");
  });

  describe("Loading_and_error_states", () => {
    it("shows a busy skeleton with a spoken status while loading, and asks for nothing before sign-in is known", () => {
      auth.status = "loading";
      session.present = false;
      renderView();

      expect(screen.getByRole("status")).toHaveTextContent("Đang tải vé…");
      expect(document.querySelector("[aria-busy]")).not.toBeNull();
      expect(api.getMyTickets).not.toHaveBeenCalled();
    });

    it("shows an error with a retry that loads again", async () => {
      const user = userEvent.setup();
      api.getMyTickets.mockRejectedValueOnce(new ApiError(500, {})).mockResolvedValueOnce([group(1, [ticket(1)])]);
      renderView();

      expect(await screen.findByText("Không tải được vé của bạn.")).toBeInTheDocument();
      expect(screen.getByRole("tab", { name: "Sắp diễn ra" })).toBeInTheDocument();
      await user.click(screen.getByRole("button", { name: "Thử lại" }));

      expect(await screen.findByRole("listitem")).toBeInTheDocument();
    });

    it("tells apart a lost connection from a server failure", async () => {
      api.getMyTickets.mockRejectedValue(new NetworkError());
      renderView();

      expect(await screen.findByText("Không kết nối được máy chủ. Kiểm tra kết nối mạng và thử lại.")).toBeInTheDocument();
    });

    it("sends a 401 to login with a message, and returns here afterwards", async () => {
      api.getMyTickets.mockRejectedValue(new ApiError(401, {}));
      renderView();

      expect(await screen.findByText("Phiên đăng nhập đã hết hạn. Đang chuyển đến trang đăng nhập…")).toBeInTheDocument();
      expect(router.replace).toHaveBeenCalledWith("/login?returnUrl=%2Faccount%2Ftickets");
      expect(screen.queryByRole("button", { name: "Thử lại" })).not.toBeInTheDocument();
    });

    it("keeps the scope in the return url of a 401 on the past tab", async () => {
      url.state.search = "?scope=past";
      api.getMyTickets.mockRejectedValue(new ApiError(401, {}));
      renderView();

      await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/login?returnUrl=%2Faccount%2Ftickets%3Fscope%3Dpast"));
    });

    it("sends a signed-out visitor to login", () => {
      auth.status = "anonymous";
      session.present = false;
      renderView();

      expect(router.replace).toHaveBeenCalledWith("/login?returnUrl=%2Faccount%2Ftickets");
    });
  });

  describe("Tab_state_is_in_the_url_and_keyboard_operable", () => {
    it("starts on the past tab when the url says scope=past", async () => {
      url.state.search = "?scope=past";
      api.getMyTickets.mockResolvedValue([pastGroup(2, [ticket(2)])]);
      renderView();

      await screen.findByRole("listitem");
      expect(api.getMyTickets).toHaveBeenCalledWith("past", expect.anything());
      expect(api.getMyTickets).not.toHaveBeenCalledWith("upcoming", expect.anything());
      expect(screen.getByRole("tab", { name: "Đã qua" })).toHaveAttribute("aria-selected", "true");
    });

    it("falls back to upcoming for an unknown scope", async () => {
      url.state.search = "?scope=bogus";
      api.getMyTickets.mockResolvedValue([]);
      renderView();

      await screen.findByText("Bạn chưa có vé nào sắp diễn ra");
      expect(screen.getByRole("tab", { name: "Sắp diễn ra" })).toHaveAttribute("aria-selected", "true");
    });

    it("writes the scope to the url when a tab is chosen, and drops it for the default", async () => {
      const user = userEvent.setup();
      api.getMyTickets.mockResolvedValue([]);
      renderView();
      await screen.findByText("Bạn chưa có vé nào sắp diễn ra");

      await user.click(screen.getByRole("tab", { name: "Đã qua" }));
      expect(router.replace).toHaveBeenLastCalledWith("/account/tickets?scope=past", { scroll: false });

      await user.click(screen.getByRole("tab", { name: "Sắp diễn ra" }));
      expect(router.replace).toHaveBeenLastCalledWith("/account/tickets", { scroll: false });
    });

    it("is a tablist whose tabs move with the arrow keys and whose panel is labelled by the tab", async () => {
      const user = userEvent.setup();
      api.getMyTickets.mockResolvedValue([]);
      renderView();
      await screen.findByText("Bạn chưa có vé nào sắp diễn ra");

      expect(screen.getByRole("tablist", { name: "Vé của tôi" })).toBeInTheDocument();
      const upcoming = screen.getByRole("tab", { name: "Sắp diễn ra" });
      const past = screen.getByRole("tab", { name: "Đã qua" });
      expect(screen.getByRole("tabpanel")).toHaveAccessibleName("Sắp diễn ra");

      // One tab stop: Tab lands on the selected tab, the arrows move from there.
      await user.tab();
      await waitFor(() => expect(upcoming).toHaveFocus());
      await user.keyboard("{ArrowRight}");
      await waitFor(() => expect(past).toHaveFocus());
      expect(past).toHaveAttribute("aria-selected", "true");
      expect(router.replace).toHaveBeenLastCalledWith("/account/tickets?scope=past", { scroll: false });
      await screen.findByText("Bạn chưa có vé nào ở các sự kiện đã qua");

      await user.keyboard("{ArrowLeft}");
      await waitFor(() => expect(upcoming).toHaveFocus());
      expect(upcoming).toHaveAttribute("aria-selected", "true");
    });

    it("announces what a tab shows once it has loaded", async () => {
      const user = userEvent.setup();
      api.getMyTickets.mockImplementation(async (scope: string) =>
        scope === "upcoming" ? [group(1, [ticket(1)])] : [pastGroup(2, [ticket(2)]), pastGroup(3, [ticket(3)])],
      );
      renderView();

      await screen.findByRole("listitem");
      expect(screen.getByRole("status")).toHaveTextContent("Sắp diễn ra: 1 sự kiện");

      await user.click(screen.getByRole("tab", { name: "Đã qua" }));
      await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Đã qua: 2 sự kiện"));
    });

    it("focuses the heading once the first answer is in", async () => {
      api.getMyTickets.mockResolvedValue([group(1, [ticket(1)])]);
      renderView();

      const heading = await screen.findByRole("heading", { level: 1, name: "Vé của tôi" });
      await waitFor(() => expect(heading).toHaveFocus());
    });
  });
});
