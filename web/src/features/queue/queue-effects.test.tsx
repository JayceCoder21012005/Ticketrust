import { act, render, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ShoppingTimer } from "@/components/shared/shopping-timer";
import { saveAdmission } from "@/lib/queue/admission-store";
import { useAdmittedAlerts, useTabTitle, useWakeLock } from "./use-queue-effects";

const toast = vi.hoisted(() => ({ error: vi.fn() }));
vi.mock("sonner", () => ({ toast }));

describe("useTabTitle", () => {
  beforeEach(() => {
    document.title = "Phòng chờ";
  });

  it("puts the place in the tab title and restores the old title afterwards", () => {
    const { rerender, unmount } = renderHook(({ position }) => useTabTitle(position), { initialProps: { position: 1234 as number | null } });
    expect(document.title).toBe("(#1.234) TicketRush");

    rerender({ position: 99 });
    expect(document.title).toBe("(#99) TicketRush");

    unmount();
    expect(document.title).toBe("Phòng chờ");
  });

  it("leaves the title alone without a position", () => {
    renderHook(() => useTabTitle(null));

    expect(document.title).toBe("Phòng chờ");
  });
});

describe("useAdmittedAlerts", () => {
  const notify = vi.fn();

  beforeEach(() => {
    notify.mockClear();
    vi.stubGlobal("Notification", Object.assign(notify, { permission: "granted" }));
    document.title = "(#5) TicketRush";
  });
  afterEach(() => vi.unstubAllGlobals());

  it("sets the title, plays the chime and notifies a hidden tab when admitted", () => {
    const chime = vi.fn();
    vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");

    renderHook(() => useAdmittedAlerts({ admitted: true, eventTitle: "Show", chime }));

    expect(document.title).toBe("Đến lượt bạn! — TicketRush");
    expect(chime).toHaveBeenCalledTimes(1);
    expect(notify).toHaveBeenCalledWith("Đến lượt bạn!", expect.objectContaining({ body: "Mở TicketRush để chọn vé cho Show." }));
  });

  it("does nothing while not admitted, and only alerts once", () => {
    const chime = vi.fn();
    const { rerender } = renderHook(({ admitted }) => useAdmittedAlerts({ admitted, eventTitle: "Show", chime }), { initialProps: { admitted: false } });
    expect(chime).not.toHaveBeenCalled();

    rerender({ admitted: true });
    rerender({ admitted: true });

    expect(chime).toHaveBeenCalledTimes(1);
  });

  it("survives a browser that has no Notification API or blocks audio", () => {
    vi.stubGlobal("Notification", undefined);
    const chime = vi.fn(() => {
      throw new Error("autoplay blocked");
    });

    expect(() => renderHook(() => useAdmittedAlerts({ admitted: true, eventTitle: "Show", chime }))).not.toThrow();
  });
});

describe("useWakeLock", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("holds a screen wake lock while waiting and releases it afterwards", async () => {
    const release = vi.fn(async () => {});
    const request = vi.fn(async () => ({ release, addEventListener: vi.fn() }));
    vi.stubGlobal("navigator", { ...navigator, wakeLock: { request } });

    const { unmount } = renderHook(() => useWakeLock(true));
    await act(async () => {});
    expect(request).toHaveBeenCalledWith("screen");

    unmount();
    await act(async () => {});
    expect(release).toHaveBeenCalled();
  });

  it("is a no-op where the API is missing", () => {
    expect(() => renderHook(() => useWakeLock(true))).not.toThrow();
  });
});

describe("ShoppingTimer", () => {
  beforeEach(() => {
    localStorage.clear();
    toast.error.mockClear();
    vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval", "setTimeout", "clearTimeout"] });
    vi.setSystemTime(new Date("2026-12-01T11:00:00Z")); // device clock one hour fast
    // The server clock is an hour behind the device.
    vi.stubGlobal("fetch", vi.fn(async () => Response.json({ serverNow: "2026-12-01T10:00:00Z" })));
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("shows the remaining shopping window on server time and links to the seats", async () => {
    saveAdmission({ eventId: "e1", slug: "show", token: "t", expiresAt: "2026-12-01T10:10:00Z" });
    const { getByRole } = render(<ShoppingTimer />);

    await act(async () => vi.advanceTimersByTimeAsync(10));

    expect(getByRole("timer")).toHaveTextContent("10:00");
    expect(getByRole("link", { name: /Chọn ghế/ }).getAttribute("href")).toBe("/events/show/seats");

    await act(async () => vi.advanceTimersByTimeAsync(60_000));
    expect(getByRole("timer")).toHaveTextContent("09:00");
  });

  it("renders nothing without an admission", async () => {
    const { container } = render(<ShoppingTimer />);
    await act(async () => vi.advanceTimersByTimeAsync(10));

    expect(container).toBeEmptyDOMElement();
  });

  it("disappears with a toast when the window runs out", async () => {
    saveAdmission({ eventId: "e1", slug: "show", token: "t", expiresAt: "2026-12-01T10:00:05Z" });
    const { container } = render(<ShoppingTimer />);
    await act(async () => vi.advanceTimersByTimeAsync(10));
    expect(container).not.toBeEmptyDOMElement();

    await act(async () => vi.advanceTimersByTimeAsync(6_000));

    expect(container).toBeEmptyDOMElement();
    expect(toast.error).toHaveBeenCalledWith("Đã hết thời gian mua vé. Vào lại phòng chờ để tiếp tục.");
  });
});
