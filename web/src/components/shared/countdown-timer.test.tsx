import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CountdownTimer } from "./countdown-timer";

describe("CountdownTimer", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    // Device clock is 5 minutes slow compared with the server.
    vi.setSystemTime(new Date("2026-11-03T10:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("counts down on server time even when the device clock is off", () => {
    render(<CountdownTimer expiresAt="2026-11-03T10:15:00Z" serverNow="2026-11-03T10:05:00Z" />);

    expect(screen.getByRole("timer")).toHaveTextContent("10:00");
  });

  it("ticks every second", () => {
    render(<CountdownTimer expiresAt="2026-11-03T10:15:00Z" serverNow="2026-11-03T10:05:00Z" />);

    act(() => vi.advanceTimersByTime(3_000));

    expect(screen.getByRole("timer")).toHaveTextContent("09:57");
  });

  it("only announces to screen readers in the last 60 seconds", () => {
    render(<CountdownTimer expiresAt="2026-11-03T10:06:30Z" serverNow="2026-11-03T10:05:00Z" />);
    expect(screen.getByRole("timer")).toHaveAttribute("aria-live", "off");

    act(() => vi.advanceTimersByTime(31_000));

    expect(screen.getByRole("timer")).toHaveAttribute("aria-live", "polite");
  });

  it("calls onExpire exactly once", () => {
    const onExpire = vi.fn();
    render(<CountdownTimer expiresAt="2026-11-03T10:05:02Z" serverNow="2026-11-03T10:05:00Z" onExpire={onExpire} />);

    act(() => vi.advanceTimersByTime(5_000));

    expect(onExpire).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("timer")).toHaveTextContent("00:00");
  });
});
