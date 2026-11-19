import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SearchSuggestions } from "@/lib/catalog/search";

const push = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ push }) }));

const api = vi.hoisted(() => ({ fetchSuggestions: vi.fn() }));
vi.mock("@/lib/catalog/search", async (original) => ({ ...(await original<object>()), fetchSuggestions: api.fetchSuggestions }));

import { highlight, SearchPalette } from "./search-palette";
import { SearchTrigger } from "./search-trigger";

const suggestions: SearchSuggestions = {
  events: [{ slug: "son-tung-mtp-live", title: "Sơn Tùng M-TP Live", startsAt: "2026-12-31T13:00:00Z", venueName: "Sân vận động Mỹ Đình", city: "Hà Nội", coverImageUrl: null }],
  artists: ["Sơn Tùng M-TP"],
  venues: [{ name: "Sân vận động Mỹ Đình", city: "Hà Nội" }],
  cities: ["Hà Nội"],
};

beforeEach(() => api.fetchSuggestions.mockResolvedValue(suggestions));
afterEach(() => {
  push.mockReset();
  api.fetchSuggestions.mockReset();
  localStorage.clear();
});

describe("highlight", () => {
  it("marks the matched words whatever the accents and case", () => {
    const { container } = render(<span>{highlight("Sơn Tùng M-TP Live", "son tung")}</span>);

    expect([...container.querySelectorAll("mark")].map((m) => m.textContent)).toEqual(["Sơn", "Tùng"]);
  });
});

describe("SearchPalette", () => {
  it("groups events, artists, venues and cities with headings", async () => {
    render(<SearchPalette open onOpenChange={() => {}} />);

    await userEvent.type(screen.getByRole("combobox"), "son tung");

    for (const heading of ["Sự kiện", "Nghệ sĩ", "Địa điểm", "Thành phố"]) {
      expect(await screen.findByText(heading)).toBeInTheDocument();
    }
    expect(screen.getByRole("status")).toHaveTextContent("4 kết quả");
  });

  it("opens the highlighted event with Enter and remembers the search", async () => {
    render(<SearchPalette open onOpenChange={() => {}} />);

    await userEvent.type(screen.getByRole("combobox"), "son tung");
    await screen.findByText("Nghệ sĩ");
    await userEvent.keyboard("{Enter}");

    expect(push).toHaveBeenCalledWith("/events/son-tung-mtp-live");
    expect(JSON.parse(localStorage.getItem("tr-recent-searches")!)).toEqual(["son tung"]);
  });

  it("offers every result on the listing page", async () => {
    render(<SearchPalette open onOpenChange={() => {}} />);

    await userEvent.type(screen.getByRole("combobox"), "son tung");
    await userEvent.click(await screen.findByText(/Xem tất cả kết quả/));

    expect(push).toHaveBeenCalledWith("/events?q=son+tung");
  });

  it("shows recent searches on an empty input, and clears them", async () => {
    localStorage.setItem("tr-recent-searches", JSON.stringify(["den vau"]));
    render(<SearchPalette open onOpenChange={() => {}} />);

    expect(await screen.findByText("den vau")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Xoá lịch sử tìm kiếm" }));

    expect(screen.queryByText("den vau")).not.toBeInTheDocument();
  });
});

describe("SearchTrigger", () => {
  it.each([{ metaKey: true }, { ctrlKey: true }])("opens the palette with %o + K", async (modifier) => {
    render(<SearchTrigger variant="header" />);

    document.dispatchEvent(new KeyboardEvent("keydown", { key: "k", ...modifier, bubbles: true }));

    expect(await screen.findByRole("combobox")).toBeInTheDocument();
  });

  it("opens on click and closes with Escape", async () => {
    render(<SearchTrigger variant="hero" />);

    await userEvent.click(screen.getByRole("button", { name: /Tìm sự kiện/ }));
    expect(await screen.findByRole("combobox")).toBeInTheDocument();
    await userEvent.keyboard("{Escape}");

    await waitFor(() => expect(screen.queryByRole("combobox")).not.toBeInTheDocument());
  });
});
