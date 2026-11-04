import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { eventFixture } from "./steps.fixtures";
import { Stepper } from "./stepper";

describe("Stepper", () => {
  it("links every step and shows ✓ / ! / ○ with an accessible status", () => {
    render(
      <Stepper
        eventId="e1"
        current="tiers"
        event={eventFixture({ coverImageUrl: "x" })}
        checklist={{ ready: false, failures: [{ code: "publish.no_tiers", step: "tiers", message: "" }] }}
      />,
    );

    expect(screen.getByRole("link", { name: /Thông tin.*Đã xong/ })).toHaveAttribute("href", "/organizer/events/e1/info");
    const current = screen.getByRole("link", { name: /Hạng vé.*Cần sửa/ });
    expect(current).toHaveAttribute("aria-current", "step");
    expect(screen.getByRole("link", { name: /Sơ đồ.*Chưa làm/ })).toBeInTheDocument();
    expect(screen.getAllByRole("link")).toHaveLength(5);
  });
});
