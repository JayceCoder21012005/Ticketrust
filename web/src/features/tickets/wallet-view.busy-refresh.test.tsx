import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { NetworkError } from "@/lib/errors";
import type { TicketDetail } from "@/lib/tickets/types";

const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn(), prefetch: vi.fn() }));
vi.mock("next/navigation", () => ({ useRouter: () => router, usePathname: () => "/tickets/x" }));

/** The real cache module over an in-memory store (jsdom has no IndexedDB). */
vi.mock("@/lib/tickets/key-cache", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/tickets/key-cache")>();
  const { memoryKeyStore } = await import("@/lib/tickets/fixtures/memory-key-store");
  return { ...actual, ...actual.createKeyCache(memoryKeyStore()) };
});
vi.mock("@/lib/tickets/register-sw", () => ({ registerTicketWorker: vi.fn(), forgetOfflineTickets: vi.fn(async () => undefined) }));

const api = vi.hoisted(() => ({ getTicket: vi.fn(), getQrKey: vi.fn(), getMyTickets: vi.fn() }));
vi.mock("@/lib/tickets/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("@/lib/tickets/api")>()), ...api }));
vi.mock("@/lib/server-time", () => ({ fetchServerTime: vi.fn(async () => ({ serverNow: new Date() })) }));

const { WalletView } = await import("./wallet-view");
const { AuthProvider } = await import("@/components/auth/auth-provider");
const { keyCache, ticketCache } = await import("@/lib/tickets/key-cache");
const { clearSession } = await import("@/lib/api/session");

const ID = "0192f0e0-0002-7bbb-8ccc-0123456789ab";
const SECRET = btoa(String.fromCharCode(...new Uint8Array(32).fill(42)));
const ENDS_AT = new Date(Date.now() + 3_600_000).toISOString();

const cached: TicketDetail = {
  id: ID,
  orderId: "o1",
  tierName: "Hạng VIP",
  seat: { section: "A", row: "3", number: "12" },
  status: "Valid",
  holderName: "Nguyễn Văn An",
  orderTicketIds: [ID],
  event: {
    eventId: "e1",
    slug: "dem-nhac",
    title: "Đêm nhạc Mùa Thu",
    coverImageUrl: null,
    startsAt: new Date(Date.now() - 600_000).toISOString(),
    endsAt: ENDS_AT,
    venueName: "Nhà hát Hòa Bình",
    city: "TP.HCM",
    status: "Published",
  },
};

const fetchMock = vi.fn<typeof fetch>();

beforeEach(async () => {
  vi.stubGlobal("fetch", fetchMock);
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: false, media: query, addEventListener: () => undefined, removeEventListener: () => undefined }));
  window.localStorage.clear();
  document.cookie = "tr_session=1; path=/";
  router.replace.mockClear();
  api.getTicket.mockReset().mockRejectedValue(new NetworkError());
  api.getQrKey.mockReset().mockRejectedValue(new NetworkError());
  // This device's last user (u1) opened the ticket online, so a key and the detail are cached.
  await keyCache.clearAll();
  await keyCache.claim("u1");
  await ticketCache.put(cached);
  await keyCache.put(ID, { secret: SECRET, endsAt: ENDS_AT });
});

afterEach(() => {
  clearSession();
  fetchMock.mockReset();
  vi.unstubAllGlobals();
  document.cookie = "tr_session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
});

describe("WalletView with a busy api at the venue", () => {
  it.each([503, 429, 511])("Busy_refresh_%i_keeps_the_cached_ticket_on_screen, never the login page", async (status) => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ code: "system.busy" }), { status }));

    render(
      <AuthProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <WalletView ticketId={ID} />
        </QueryClientProvider>
      </AuthProvider>,
    );

    expect(await screen.findByText("Nguyễn Văn An")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("img", { name: "Mã QR vào cửa, đổi mỗi 15 giây" })).toBeInTheDocument());
    expect(router.replace).not.toHaveBeenCalled();
    // The device's offline data is still there.
    expect(await keyCache.get(ID)).not.toBeNull();
  });
});
