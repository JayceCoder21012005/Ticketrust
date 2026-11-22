import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EventDetail } from "@/lib/catalog/types";
// The resale list (spec 14) has its own tests; here it would need the app router.
vi.mock("@/features/resale/resale-listings", () => ({ default: () => null }));

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

  it("offers the waitlist when every tier is sold out: a login link for a visitor", async () => {
    startDeviceClock(0, 0);
    vi.setSystemTime(Date.parse(opensAt) + MIN);
    render(<EventCta event={event} priceFrom={800_000} />);
    await flush();

    expect(screen.getByRole("link", { name: "Hết vé — Vào danh sách chờ" })).toHaveAttribute("href", "/login?returnUrl=%2Fevents%2Fson-tung-mtp-live");
    expect(screen.getByText("Có vé trả lại, người chờ lâu nhất được nhận trước.")).toBeInTheDocument();
  });

  it("explains a cancelled event instead of selling it", async () => {
    startDeviceClock(0);
    render(<EventCta event={{ ...event, status: "Cancelled" }} priceFrom={800_000} />);
    await flush();

    expect(screen.getByRole("button", { name: "Sự kiện đã bị huỷ" })).toBeDisabled();
    expect(screen.getByText("Vé đã mua sẽ được hoàn tiền tự động.")).toBeInTheDocument();
  });

  describe("the follow button (spec 09)", () => {
    const follow = () => screen.queryByRole("link", { name: "Nhắc tôi" });
    const at = (ms: number) => vi.setSystemTime(ms);

    // Signed out in these tests, so "Nhắc tôi" is the login link; the signed-in toggle has its own tests.
    it.each([
      ["Upcoming", () => at(serverNow), null],
      ["PreQueue", () => at(Date.parse(opensAt) - 10 * MIN), "Vào phòng chờ"],
      ["OnSale", () => at(Date.parse(opensAt) + MIN), "Mua vé"],
    ] as const)("is offered in the %s state, beside the main action", async (_state, arrange, primary) => {
      startDeviceClock(0);
      arrange();
      render(<EventCta event={event} priceFrom={800_000} />);
      await flush();

      expect(follow()).toHaveAttribute("href", "/login?returnUrl=%2Fevents%2Fson-tung-mtp-live");
      if (primary) {
        const buy = screen.getByRole("link", { name: primary });
        // Never above the main action: it follows it in the document, and is the quiet (ghost) one.
        expect(buy.compareDocumentPosition(follow()!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        expect(follow()).toHaveAttribute("data-variant", "ghost");
        expect(buy).not.toHaveAttribute("data-variant", "ghost");
      }
    });

    it("is offered in the SoldOut state, under the waitlist action", async () => {
      startDeviceClock(0, 0);
      at(Date.parse(opensAt) + MIN);
      render(<EventCta event={event} priceFrom={800_000} />);
      await flush();

      expect(screen.getByRole("link", { name: "Hết vé — Vào danh sách chờ" })).toBeInTheDocument();
      expect(follow()).toBeInTheDocument();
    });

    it.each<[string, typeof event, () => void]>([
      ["Ended", { ...event, status: "Ended" }, () => at(serverNow)],
      ["Cancelled", { ...event, status: "Cancelled" }, () => at(serverNow)],
      ["SalesClosed", event, () => at(Date.parse("2026-12-25T00:00:00Z"))],
      ["NotScheduled", { ...event, phases: [] }, () => at(serverNow)],
    ])("is not offered in the %s state", async (_state, forEvent, arrange) => {
      startDeviceClock(0);
      arrange();
      render(<EventCta event={forEvent} priceFrom={800_000} />);
      await flush();

      expect(screen.getAllByRole("button").length).toBeGreaterThan(0);
      expect(follow()).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /Nhắc tôi|Đã bật nhắc nhở/ })).not.toBeInTheDocument();
    });

    it.each([
      ["OnSale", 10, () => at(Date.parse(opensAt) + MIN), "link", "Mua vé"],
      ["PreQueue", 10, () => at(Date.parse(opensAt) - 10 * MIN), "link", "Vào phòng chờ"],
      ["SoldOut", 0, () => at(Date.parse(opensAt) + MIN), "link", "Hết vé — Vào danh sách chờ"],
    ] as const)("is an icon before the %s action in the mobile bar, so the main action keeps its label at the thumb's end", async (_state, remaining, arrange, role, primary) => {
      startDeviceClock(0, remaining);
      arrange();
      render(<EventCta event={event} priceFrom={800_000} variant="bar" />);
      await flush();

      const main = screen.getByRole(role, { name: primary });
      const bell = follow()!;
      expect(bell.compareDocumentPosition(main) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      expect(bell).toHaveAttribute("aria-label", "Nhắc tôi");
      expect(bell).toHaveAttribute("data-size", "icon");
      expect(bell).not.toHaveTextContent(/\S/);
      expect(main).toHaveTextContent(primary);
    });

    it("keeps its label in the panel", async () => {
      startDeviceClock(0);
      at(Date.parse(opensAt) + MIN);
      render(<EventCta event={event} priceFrom={800_000} />);
      await flush();

      expect(follow()).toHaveTextContent("Nhắc tôi");
      expect(follow()).not.toHaveAttribute("data-size", "icon");
    });

    it("keeps the reminder hint (30 minutes before the sale) to the state where that reminder is still to come", async () => {
      startDeviceClock(0);
      const { unmount } = render(<EventCta event={event} priceFrom={800_000} />);
      await flush();
      expect(screen.getByText(/trước giờ mở bán 30 phút/)).toBeInTheDocument();
      expect(screen.getByText(/24 giờ trước khi sự kiện diễn ra/)).toBeInTheDocument();
      unmount();

      at(Date.parse(opensAt) + MIN);
      render(<EventCta event={event} priceFrom={800_000} />);
      await flush();
      expect(screen.queryByText(/trước giờ mở bán 30 phút/)).not.toBeInTheDocument();
    });
  });

  it("shows the starting price", async () => {
    startDeviceClock(0);
    render(<EventCta event={event} priceFrom={800_000} />);
    await flush();

    expect(screen.getByText(/từ 800\.000/)).toBeInTheDocument();
  });
});
