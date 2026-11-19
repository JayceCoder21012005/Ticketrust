import { render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { HomeSections } from "@/lib/catalog/types";
import type { EventSummary } from "@/lib/catalog/types";

vi.mock("@/features/search/search-trigger", () => ({ SearchTrigger: () => <button type="button">Tìm sự kiện, nghệ sĩ, địa điểm</button> }));

import { HomeContent, HomeHero } from "./home-content";
import { heroPhotoFor, PHOTOS } from "./photos";

const event = (slug: string, extra: Partial<EventSummary> = {}): EventSummary => ({
  id: slug,
  slug,
  title: `Sự kiện ${slug}`,
  category: "concert",
  startsAt: "2026-12-31T13:00:00Z",
  venueName: "Nhà hát",
  city: "Hà Nội",
  coverImageUrl: null,
  priceFrom: 500_000,
  nextSaleOpensAt: null,
  isOnSale: true,
  isSoldOut: false,
  ...extra,
});

const full: HomeSections = {
  hot: [event("hot-1"), event("hot-2")],
  openingSoon: [event("soon-1", { isOnSale: false, nextSaleOpensAt: "2026-12-01T03:00:00Z" })],
  thisWeek: [event("week-1")],
  upcoming: [event("later-1")],
  cities: [{ city: "Hà Nội", events: 7 }, { city: "Đà Nẵng", events: 2 }],
  categories: [{ category: "concert", events: 5 }, { category: "sports", events: 1 }],
};

const empty: HomeSections = { hot: [], openingSoon: [], thisWeek: [], upcoming: [], cities: [], categories: [] };

afterEach(() => vi.useRealTimers());

describe("heroPhotoFor", () => {
  it("keeps one photo for a whole day and moves to the next one the day after", () => {
    const morning = heroPhotoFor(new Date("2026-11-20T01:00:00+07:00"));
    const night = heroPhotoFor(new Date("2026-11-20T23:30:00+07:00"));
    const tomorrow = heroPhotoFor(new Date("2026-11-21T08:00:00+07:00"));

    expect(night).toBe(morning);
    expect(tomorrow).not.toBe(morning);
    expect(PHOTOS).toContain(morning);
  });
});

describe("HomeContent", () => {
  it("the hero carries the headline and the search", () => {
    render(<HomeHero now={new Date("2026-11-20T03:00:00Z")} />);

    expect(screen.getByRole("heading", { level: 1, name: "Săn vé nhanh, công bằng." })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Tìm sự kiện/ })).toBeInTheDocument();
  });

  it("lists the sections in order", () => {
    render(<HomeContent home={full} />);

    expect(screen.queryByRole("heading", { level: 1 })).not.toBeInTheDocument();
    const headings = screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(headings).toEqual(["Đang hot", "Sắp mở bán", "Diễn ra tuần này", "Khám phá theo thành phố", "Khám phá theo danh mục", "Sự kiện sắp diễn ra"]);
  });

  it("marks the hot cards and keeps the countdown under the opening-soon ones", () => {
    render(<HomeContent home={full} />);

    const hot = screen.getByRole("region", { name: "Đang hot" });
    expect(within(hot).getAllByText("Đang hot", { selector: "[data-slot='badge']" })).toHaveLength(2);
    expect(within(screen.getByRole("region", { name: "Sắp mở bán" })).getByText(/Mở bán sau/)).toBeInTheDocument();
  });

  it("never renders a heading for a section without events", () => {
    render(<HomeContent home={{ ...full, hot: [], thisWeek: [] }} />);

    expect(screen.queryByRole("heading", { name: "Đang hot" })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Diễn ra tuần này" })).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Sắp mở bán" })).toBeInTheDocument();
  });

  it("shows the empty state when nothing is on", () => {
    render(<HomeContent home={empty} />);

    // The empty state's own title is the only heading under the hero: no section heading at all.
    expect(screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent)).toEqual(["Chưa có sự kiện nào đang mở"]);
  });

  it("lists cities with their event counts, linking to the filtered listing", () => {
    render(<HomeContent home={full} />);

    const link = screen.getByRole("link", { name: /Hà Nội.*7 sự kiện/ });
    expect(link).toHaveAttribute("href", "/events?city=H%C3%A0+N%E1%BB%99i");
  });

  it("keeps the category navigation with counts", () => {
    render(<HomeContent home={full} />);

    const nav = screen.getByRole("navigation", { name: "Khám phá theo danh mục" });
    expect(within(nav).getByRole("link", { name: /Thể thao/ })).toHaveAttribute("href", "/events?category=sports");
  });
});
