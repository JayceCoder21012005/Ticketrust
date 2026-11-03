import { render, screen } from "@testing-library/react";
import { renderToString } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DesktopOnly } from "./desktop-only";

function stubViewport(desktop: boolean) {
  vi.stubGlobal("matchMedia", (query: string) => ({
    matches: desktop,
    media: query,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  }));
}

describe("DesktopOnly", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("renders nothing on the server, so phones never hydrate the sidebar", () => {
    expect(renderToString(<DesktopOnly>sidebar</DesktopOnly>)).toBe("");
  });

  it("mounts its children on wide screens", () => {
    stubViewport(true);
    render(<DesktopOnly>sidebar</DesktopOnly>);

    expect(screen.getByText("sidebar")).toBeInTheDocument();
  });

  it("stays empty on phones", () => {
    stubViewport(false);
    render(<DesktopOnly>sidebar</DesktopOnly>);

    expect(screen.queryByText("sidebar")).not.toBeInTheDocument();
  });
});
