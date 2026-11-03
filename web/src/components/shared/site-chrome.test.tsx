import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "next-themes";
import { beforeAll, describe, expect, it, vi } from "vitest";
import { SiteFooter } from "./site-footer";
import { SiteHeader } from "./site-header";
import { ThemeToggle } from "./theme-toggle";

beforeAll(() => {
  // next-themes reads the system preference.
  window.matchMedia = vi.fn().mockReturnValue({
    matches: false,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  });
});

describe("SiteHeader", () => {
  it("links the logo home and offers a skip link to the main content", () => {
    render(<SiteHeader />);

    expect(screen.getByRole("link", { name: "TicketRush" })).toHaveAttribute("href", "/");
    expect(screen.getByRole("link", { name: "Bỏ qua đến nội dung chính" })).toHaveAttribute("href", "#main");
  });

  it("has a search landmark that submits to /events?q=", () => {
    render(<SiteHeader />);

    const search = screen.getByRole("search");
    expect(search).toHaveAttribute("action", "/events");
    expect(screen.getByRole("searchbox", { name: "Tìm kiếm" })).toHaveAttribute("name", "q");
  });

  it("offers sign-in", () => {
    render(<SiteHeader />);

    expect(screen.getByRole("link", { name: "Đăng nhập" })).toHaveAttribute("href", "/login");
  });
});

describe("ThemeToggle", () => {
  it("switches between light and dark", async () => {
    render(
      <ThemeProvider attribute="class" defaultTheme="light" enableSystem={false}>
        <ThemeToggle />
      </ThemeProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Đổi giao diện" }));

    expect(document.documentElement).toHaveClass("dark");
  });
});

describe("SiteFooter", () => {
  it("is the contentinfo landmark with the current year", () => {
    render(<SiteFooter />);

    expect(screen.getByRole("contentinfo")).toHaveTextContent(`© ${new Date().getFullYear()} TicketRush`);
  });
});
