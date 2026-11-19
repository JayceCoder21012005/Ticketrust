import { render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

vi.mock("@/components/auth/auth-provider", () => ({ AuthProvider: ({ children }: { children: React.ReactNode }) => children }));

import AuthLayout from "./layout";

describe("auth layout artwork", () => {
  it("shows the Bukchon photo on wide screens only, as decoration", () => {
    const { container } = render(<AuthLayout params={Promise.resolve({})}>form</AuthLayout>);

    const source = container.querySelector("aside picture source")!;
    expect(source.getAttribute("media")).toBe("(min-width: 1024px)");
    expect(source.getAttribute("srcset")).toMatch(/bukchon/);
    const img = container.querySelector("aside picture img")!;
    expect(img).toHaveAttribute("alt", "");
    expect(img.getAttribute("src")).toMatch(/^data:image\/gif/);
    expect(container.querySelector("aside")!.className).toMatch(/hidden.*lg:flex/);
  });
});
