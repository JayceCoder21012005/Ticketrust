import { act, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ActiveFilters } from "./active-filters";
import { FilterPanel } from "./filter-panel";
import { LoadMore } from "./load-more";
import { SearchBox } from "./search-box";

const replace = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace, push: vi.fn(), refresh: vi.fn() }) }));
const loadMoreEvents = vi.fn<(query: Record<string, string>, cursor: string) => Promise<{ cards: React.ReactNode; nextCursor: string | null }>>();
vi.mock("./actions", () => ({ loadMoreEvents: (query: Record<string, string>, cursor: string) => loadMoreEvents(query, cursor) }));

const cities = ["Đà Nẵng", "Hà Nội", "Hồ Chí Minh"];

beforeEach(() => {
  replace.mockReset();
  loadMoreEvents.mockReset();
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("FilterPanel", () => {
  it("puts the chosen category in the URL and keeps the other filters", async () => {
    render(<FilterPanel filters={{ q: "derby", city: "Hà Nội" }} cities={cities} />);

    await userEvent.click(within(screen.getByRole("group", { name: "Danh mục" })).getByLabelText("Thể thao"));

    expect(replace).toHaveBeenCalledWith("/events?q=derby&category=sports&city=H%C3%A0+N%E1%BB%99i", { scroll: false });
  });

  it("'Tất cả' removes the filter", async () => {
    render(<FilterPanel filters={{ category: "sports" }} cities={cities} />);

    await userEvent.click(within(screen.getByRole("group", { name: "Danh mục" })).getByLabelText("Tất cả"));

    expect(replace).toHaveBeenCalledWith("/events", { scroll: false });
  });

  it("a date preset replaces custom dates", async () => {
    render(<FilterPanel filters={{ from: "2026-12-01", to: "2026-12-05" }} cities={cities} />);

    await userEvent.click(screen.getByLabelText("Cuối tuần này"));

    expect(replace).toHaveBeenCalledWith("/events?when=weekend", { scroll: false });
  });

  it("custom dates replace a preset", () => {
    render(<FilterPanel filters={{ when: "today" }} cities={cities} />);

    fireEvent.change(screen.getByLabelText("Từ ngày"), { target: { value: "2026-12-24" } });

    expect(replace).toHaveBeenCalledWith("/events?from=2026-12-24", { scroll: false });
  });

  it("sets a maximum price", async () => {
    render(<FilterPanel filters={{}} cities={cities} />);

    await userEvent.click(screen.getByLabelText(/Đến 500\.000/));

    expect(replace).toHaveBeenCalledWith("/events?priceMax=500000", { scroll: false });
  });

  it("marks the current choices as checked", () => {
    render(<FilterPanel filters={{ city: "Đà Nẵng", when: "month" }} cities={cities} />);

    expect(screen.getByLabelText("Đà Nẵng")).toBeChecked();
    expect(screen.getByLabelText("Tháng này")).toBeChecked();
  });
});

describe("SearchBox", () => {
  it("updates the URL once, 300 ms after the user stops typing, keeping filters", async () => {
    vi.useFakeTimers();
    render(<SearchBox filters={{ category: "concert" }} />);
    const input = screen.getByRole("searchbox");

    fireEvent.change(input, { target: { value: "s" } });
    fireEvent.change(input, { target: { value: "son" } });
    act(() => vi.advanceTimersByTime(299));
    expect(replace).not.toHaveBeenCalled();

    act(() => vi.advanceTimersByTime(1));
    expect(replace).toHaveBeenCalledExactlyOnceWith("/events?q=son&category=concert", { scroll: false });
  });

  it("clearing the box removes the search", () => {
    vi.useFakeTimers();
    render(<SearchBox filters={{ q: "son" }} />);

    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "" } });
    act(() => vi.advanceTimersByTime(300));

    expect(replace).toHaveBeenCalledWith("/events", { scroll: false });
  });

  it("submitting searches immediately", () => {
    render(<SearchBox filters={{}} />);

    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "đen vâu" } });
    fireEvent.submit(screen.getByRole("search"));

    expect(replace).toHaveBeenCalledWith("/events?q=%C4%91en+v%C3%A2u", { scroll: false });
  });
});

describe("ActiveFilters", () => {
  it("links to the listing without each filter, plus 'clear all' that keeps the search text (it has its own box)", () => {
    render(<ActiveFilters filters={{ q: "son", category: "concert", city: "Hà Nội", priceMax: 500_000 }} />);

    expect(screen.getByRole("link", { name: "Bỏ lọc: Nhạc sống" })).toHaveAttribute(
      "href",
      "/events?q=son&city=H%C3%A0+N%E1%BB%99i&priceMax=500000",
    );
    expect(screen.getByRole("link", { name: "Bỏ lọc: Hà Nội" })).toHaveAttribute("href", "/events?q=son&category=concert&priceMax=500000");
    expect(screen.getByRole("link", { name: "Xoá tất cả" })).toHaveAttribute("href", "/events?q=son");
  });

  it("renders nothing without filters", () => {
    const { container } = render(<ActiveFilters filters={{}} />);

    expect(container).toBeEmptyDOMElement();
  });
});

describe("LoadMore", () => {
  it("asks the server for the next page with the cursor and appends it until there is no next cursor", async () => {
    loadMoreEvents
      .mockResolvedValueOnce({ cards: <li>Sự kiện b</li>, nextCursor: "c2" })
      .mockResolvedValueOnce({ cards: <li>Sự kiện c</li>, nextCursor: null });
    render(
      <ul>
        <LoadMore query={{ category: "sports" }} cursor="c1" />
      </ul>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Xem thêm" }));
    expect(await screen.findByText("Sự kiện b")).toBeInTheDocument();
    expect(loadMoreEvents).toHaveBeenLastCalledWith({ category: "sports" }, "c1");

    await userEvent.click(screen.getByRole("button", { name: "Xem thêm" }));
    expect(await screen.findByText("Sự kiện c")).toBeInTheDocument();
    expect(loadMoreEvents).toHaveBeenLastCalledWith({ category: "sports" }, "c2");
    expect(screen.getByText("Sự kiện b")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Xem thêm" })).not.toBeInTheDocument();
  });

  it("keeps the button and says so when a page fails", async () => {
    loadMoreEvents.mockRejectedValue(new Error("api down"));
    render(
      <ul>
        <LoadMore query={{}} cursor="c1" />
      </ul>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Xem thêm" }));

    expect(await screen.findByText("Không tải thêm được.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Xem thêm" })).toBeEnabled();
  });
});
