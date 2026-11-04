import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EventDetail } from "@/lib/catalog/types";
import { EventCta, resetAvailabilityCache } from "./event-cta";

const MIN = 60_000;
// Server time when the page is opened; the sale opens 31 minutes later.
const serverNow = Date.parse("2026-12-01T12:29:00Z");
const opensAt = new Date(serverNow + 31 * MIN).toISOString();

const event: Pick<EventDetail, "id" | "slug" | "status" | "endsAt" | "isSoldOut" | "phases"> = {
  id: "e1",
  slug: "son-tung-mtp-live",
  status: "Published",
  endsAt: "2026-12-31T16:00:00Z",
  isSoldOut: false,
  phases: [
    {
      id: "p1",
      name: "Mở bán",
      opensAt,
      closesAt: "2026-12-20T00:00:00Z",
      queueEnabled: true,
      maxTicketsPerUser: 4,
      requiresAccessCode: false,
    },
  ],
};

/** The api answers with server time; the device clock is `skewMs` off from it. */
function startDeviceClock(skewMs: number, remaining = 10) {
  vi.setSystemTime(serverNow + skewMs);
  vi.stubGlobal(
    "fetch",
    vi.fn(async () =>
      Response.json({
        serverNow: new Date(Date.now() - skewMs).toISOString(),
        version: 0,
        unavailable: [],
        tiers: [{ tierId: "t1", remaining }],
      }),
    ),
  );
}

async function flush() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(0);
  });
}

describe("EventCta", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    resetAvailabilityCache();
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  for (const [label, skew] of [
    ["is right", 0],
    ["is 5 minutes slow", -5 * MIN],
    ["is 5 minutes fast", 5 * MIN],
  ] as const) {
    it(`switches from the countdown to "Vào phòng chờ" at T-30 without a reload when the device clock ${label}`, async () => {
      startDeviceClock(skew);
      render(<EventCta event={event} priceFrom={800_000} />);
      await flush();

      expect(screen.getByText("Mở bán sau")).toBeInTheDocument();
      expect(screen.queryByRole("link", { name: "Vào phòng chờ" })).not.toBeInTheDocument();

      await act(async () => {
        await vi.advanceTimersByTimeAsync(MIN - 1_000);
      });
      expect(screen.queryByRole("link", { name: "Vào phòng chờ" })).not.toBeInTheDocument();

      await act(async () => {
        await vi.advanceTimersByTimeAsync(1_500);
      });
      expect(screen.getByRole("link", { name: "Vào phòng chờ" })).toHaveAttribute("href", "/events/son-tung-mtp-live/queue");
    });
  }

  it("shows nothing actionable until the server clock is known", () => {
    startDeviceClock(0);
    render(<EventCta event={event} priceFrom={800_000} />);

    expect(screen.queryByRole("link")).not.toBeInTheDocument();
    expect(screen.queryByText("Mở bán sau")).not.toBeInTheDocument();
  });

  it("buys through the waiting room while a queued phase is open", async () => {
    startDeviceClock(0);
    vi.setSystemTime(Date.parse(opensAt) + MIN);
    render(<EventCta event={event} priceFrom={800_000} />);
    await flush();

    expect(screen.getByRole("link", { name: "Mua vé" })).toHaveAttribute("href", "/events/son-tung-mtp-live/queue");
  });

  it("goes through the waiting room even when the open phase has no queue (it admits at once and hands out the admission)", async () => {
    startDeviceClock(0);
    vi.setSystemTime(Date.parse(opensAt) + MIN);
    const noQueue = { ...event, phases: [{ ...event.phases[0], queueEnabled: false }] };
    render(<EventCta event={noQueue} priceFrom={800_000} />);
    await flush();

    expect(screen.getByRole("link", { name: "Mua vé" })).toHaveAttribute("href", "/events/son-tung-mtp-live/queue");
  });

  it("is a disabled waitlist button when every tier is sold out", async () => {
    startDeviceClock(0, 0);
    vi.setSystemTime(Date.parse(opensAt) + MIN);
    render(<EventCta event={event} priceFrom={800_000} />);
    await flush();

    expect(screen.getByRole("button", { name: "Hết vé — Vào danh sách chờ" })).toBeDisabled();
  });

  it("explains a cancelled event instead of selling it", async () => {
    startDeviceClock(0);
    render(<EventCta event={{ ...event, status: "Cancelled" }} priceFrom={800_000} />);
    await flush();

    expect(screen.getByRole("button", { name: "Sự kiện đã bị huỷ" })).toBeDisabled();
    expect(screen.getByText("Vé đã mua sẽ được hoàn tiền tự động.")).toBeInTheDocument();
  });

  it("shows the starting price", async () => {
    startDeviceClock(0);
    render(<EventCta event={event} priceFrom={800_000} />);
    await flush();

    expect(screen.getByText(/từ 800\.000/)).toBeInTheDocument();
  });
});
