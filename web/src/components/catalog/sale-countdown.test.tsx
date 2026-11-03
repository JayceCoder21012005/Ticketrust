import { act, render, screen } from "@testing-library/react";
import { renderToString } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SaleCountdown } from "./sale-countdown";

describe("SaleCountdown", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("never reads the clock while rendering, so the page around it can be prerendered", () => {
    const now = vi.spyOn(Date, "now");

    const html = renderToString(<SaleCountdown opensAt="2026-12-01T13:00:00Z" />);

    expect(now).not.toHaveBeenCalled();
    expect(html).toContain('aria-hidden="true"');
  });

  it("counts down on server time once the offset is known", async () => {
    vi.useFakeTimers();
    // Device clock is 5 minutes slow: the server says 12:55, the sale opens at 13:00.
    vi.setSystemTime(new Date("2026-12-01T12:50:00Z"));
    vi.stubGlobal("fetch", vi.fn(async () => Response.json({ serverNow: "2026-12-01T12:55:00Z" })));
    vi.resetModules();
    const { SaleCountdown: Fresh } = await import("./sale-countdown");

    render(<Fresh opensAt="2026-12-01T13:00:00Z" />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1_000);
    });

    expect(screen.getByText(/^04:5\d$/)).toBeInTheDocument();
  });
});
