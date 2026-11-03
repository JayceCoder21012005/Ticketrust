import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { EmptyState } from "./empty-state";
import { ErrorState } from "./error-state";
import { EventCard } from "./event-card";
import { PageHeader } from "./page-header";
import { Price } from "./price";
import { StickyActionBar } from "./sticky-action-bar";

const plain = (s: string | null) => (s ?? "").replace(/ | /g, " ");

describe("Price", () => {
  it("shows the all-in VND amount", () => {
    render(<Price amount={1_500_000} />);

    expect(plain(screen.getByText(/₫/).textContent)).toBe("1.500.000 ₫");
  });

  it("can be prefixed with 'từ' for a starting price", () => {
    render(<Price amount={800_000} from />);

    expect(plain(screen.getByText(/₫/).textContent)).toBe("từ 800.000 ₫");
  });
});

describe("ErrorState", () => {
  it("is announced as an alert and retries on click", async () => {
    const onRetry = vi.fn();
    render(<ErrorState onRetry={onRetry} />);

    expect(screen.getByRole("alert")).toHaveTextContent("Không tải được dữ liệu");
    await userEvent.click(screen.getByRole("button", { name: "Thử lại" }));

    expect(onRetry).toHaveBeenCalledTimes(1);
  });
});

describe("EmptyState", () => {
  it("shows a title, description and optional action", () => {
    render(<EmptyState title="Chưa có vé" description="Mua vé để thấy ở đây" action={<button type="button">Khám phá</button>} />);

    expect(screen.getByRole("heading", { name: "Chưa có vé" })).toBeInTheDocument();
    expect(screen.getByText("Mua vé để thấy ở đây")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Khám phá" })).toBeInTheDocument();
  });
});

describe("EventCard", () => {
  const event = {
    slug: "son-tung-mtp-live",
    title: "Sơn Tùng M-TP Live",
    startsAt: "2026-12-31T13:00:00Z",
    venueName: "Sân vận động Mỹ Đình",
    city: "Hà Nội",
    coverImageUrl: null,
    priceFrom: 800_000,
  };

  it("links to the event page with the title as its accessible name", () => {
    render(<EventCard event={event} />);

    expect(screen.getByRole("link", { name: /Sơn Tùng M-TP Live/ })).toHaveAttribute("href", "/events/son-tung-mtp-live");
  });

  it("shows the date in Vietnam time, the venue and the starting price", () => {
    render(<EventCard event={event} />);

    expect(screen.getByText(/20:00/)).toBeInTheDocument();
    expect(screen.getByText(/Sân vận động Mỹ Đình/)).toBeInTheDocument();
    expect(plain(screen.getByText(/₫/).textContent)).toBe("từ 800.000 ₫");
  });
});

describe("PageHeader", () => {
  it("renders the page title as the single h1", () => {
    render(<PageHeader title="Sự kiện" description="Tất cả sự kiện sắp diễn ra" />);

    expect(screen.getByRole("heading", { level: 1, name: "Sự kiện" })).toBeInTheDocument();
  });
});

describe("StickyActionBar", () => {
  it("is a labelled region holding the call to action", () => {
    render(
      <StickyActionBar label="Mua vé">
        <button type="button">Mua vé</button>
      </StickyActionBar>,
    );

    expect(screen.getByRole("region", { name: "Mua vé" })).toContainElement(screen.getByRole("button"));
  });
});
