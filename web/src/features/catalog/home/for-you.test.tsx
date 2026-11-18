import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const api = vi.hoisted(() => ({ GET: vi.fn() }));
vi.mock("@/lib/api/client", () => ({ api }));

import { ForYou } from "./for-you";

const summary = (slug: string, category: string) => ({
  id: slug,
  slug,
  title: `Sự kiện ${slug}`,
  category,
  startsAt: "2026-12-31T13:00:00Z",
  venueName: "Nhà hát",
  city: "Hà Nội",
  coverImageUrl: null,
  priceFrom: 500_000,
  nextSaleOpensAt: null,
  isOnSale: true,
  isSoldOut: false,
});

function respond(follows: unknown[], tickets: unknown, listing: unknown[]) {
  api.GET.mockImplementation((path: string) => {
    if (path === "/api/v1/me/follows") return Promise.resolve({ data: follows });
    if (path === "/api/v1/me/tickets") return Promise.resolve({ data: tickets });
    return Promise.resolve({ data: { items: listing, nextCursor: null } });
  });
}

afterEach(() => {
  api.GET.mockReset();
  document.cookie = "tr_session=; max-age=0; path=/";
});

describe("ForYou", () => {
  it("renders nothing for a visitor who is not signed in", () => {
    const { container } = render(<ForYou />);

    expect(container).toBeEmptyDOMElement();
    expect(api.GET).not.toHaveBeenCalled();
  });

  it("renders nothing when the buyer follows nothing", async () => {
    document.cookie = "tr_session=1; path=/";
    respond([], { events: [] }, []);
    const { container } = render(<ForYou />);

    await waitFor(() => expect(api.GET).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });

  it("suggests events in the followed categories, minus the followed ones and the ones already ticketed", async () => {
    document.cookie = "tr_session=1; path=/";
    respond(
      [summary("followed", "concert")],
      { events: [{ slug: "ticketed" }] },
      [summary("followed", "concert"), summary("ticketed", "concert"), summary("new-one", "concert")],
    );
    render(<ForYou />);

    expect(await screen.findByRole("heading", { level: 2, name: "Dành cho bạn" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Sự kiện new-one/ })).toHaveAttribute("href", "/events/new-one");
    expect(screen.queryByRole("link", { name: /Sự kiện followed/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /Sự kiện ticketed/ })).not.toBeInTheDocument();
  });
});
