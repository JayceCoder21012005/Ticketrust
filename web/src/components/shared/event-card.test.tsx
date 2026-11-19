import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { EventCard, posterName, type EventCardData } from "./event-card";

const base: EventCardData = {
  slug: "son-tung-mtp-live",
  title: "Sơn Tùng M-TP Live",
  startsAt: "2026-12-31T13:00:00Z",
  venueName: "Sân vận động Mỹ Đình",
  city: "Hà Nội",
  coverImageUrl: null,
  priceFrom: 800_000,
  category: "concert",
};

const card = () => screen.getByRole("link", { name: /Sơn Tùng M-TP Live/ });

describe("EventCard v2", () => {
  it("is one link named by the title, with the price in the last row", () => {
    render(<EventCard event={base} />);

    expect(card()).toHaveAttribute("href", "/events/son-tung-mtp-live");
    const rows = card().querySelectorAll("[data-slot='card-content'] > *, [data-slot='card-footer']");
    expect(rows[rows.length - 1]).toHaveTextContent(/800\.000/);
  });

  it.each([
    [{ isOnSale: true }, "Đang mở bán"],
    [{ nextSaleOpensAt: "2026-12-20T03:00:00Z" }, /Mở bán/],
    [{ isSoldOut: true }, "Hết vé"],
    [{ isOnSale: true, isHot: true }, "Đang hot"],
  ] as const)("shows the status chip for %o", (state, label) => {
    render(<EventCard event={{ ...base, ...state }} />);

    expect(card()).toHaveTextContent(label);
  });

  it("marks a sold-out card and greys it out", () => {
    render(<EventCard event={{ ...base, isSoldOut: true }} />);

    expect(card()).toHaveAttribute("data-sold-out");
    expect(card().className).toMatch(/grayscale/);
  });

  it("lights its outline in the brand colour on hover and focus", () => {
    render(<EventCard event={base} />);

    expect(card().querySelector("[data-slot='card']")!.className).toMatch(/group-hover\/event:ring-primary/);
  });

  it.each(["default", "large", "compact"] as const)("renders the %s variant", (variant) => {
    render(<EventCard event={base} variant={variant} />);

    expect(card()).toHaveAttribute("data-variant", variant);
  });

  it("still renders the poster when it takes part in the poster morph", () => {
    render(<EventCard event={base} posterTransitionName={posterName(base.slug)} />);

    expect(card().querySelector("[data-poster]")).not.toBeNull();
  });

  it("names a poster the same way on cards and on the event page", () => {
    expect(posterName("son-tung-mtp-live")).toBe("poster-son-tung-mtp-live");
  });
});
