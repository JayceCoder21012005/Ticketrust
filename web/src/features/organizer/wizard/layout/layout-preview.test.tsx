import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { generateSeats } from "@/lib/organizer/seat-layout";
import type { SectionDefinition, TierItem } from "../../types";
import { LayoutPreview } from "./layout-preview";
import { PRESETS } from "./presets";

const tiers: TierItem[] = [
  { id: "t1", name: "Ghế", price: 1, kind: "Seated", capacity: null, color: "#7c3aed" },
  { id: "t2", name: "Đứng", price: 1, kind: "GeneralAdmission", capacity: 100, color: "#0f766e" },
];

const big: SectionDefinition = { code: "A", tierId: "t1", kind: "seated", rows: 20, seatsPerRow: 30, origin: { x: 100, y: 80 }, curve: 0.15 };

function renderPreview(sections: SectionDefinition[], onSectionClick = vi.fn()) {
  return render(<LayoutPreview sections={sections} tiers={tiers} seats={generateSeats({ sections })} activeSection={null} onSectionClick={onSectionClick} />);
}

describe("LayoutPreview", () => {
  // The < 100 ms render budget is measured in a real browser (e2e/organizer.spec.ts): jsdom timings swing with the
  // load of parallel test workers. Here: the seat maths stays far below the budget and every seat is drawn.
  it("computes a 20x30 section well within the 100 ms budget and draws all 600 seats", () => {
    generateSeats({ sections: [big] });
    const runs = Array.from({ length: 5 }, () => {
      const started = performance.now();
      generateSeats({ sections: [big] });
      return performance.now() - started;
    });

    expect(runs.sort((a, b) => a - b)[2]).toBeLessThan(20);
    const { container } = renderPreview([big]);
    expect(container.querySelectorAll("circle")).toHaveLength(600);
  });

  it("colours seats by tier and draws standing areas as a labelled block", () => {
    const { container } = renderPreview([
      { code: "A", tierId: "t1", kind: "seated", rows: 1, seatsPerRow: 2, origin: { x: 0, y: 200 }, curve: 0 },
      { code: "GA", tierId: "t2", kind: "standing", origin: { x: 380, y: 400 }, curve: 0 },
    ]);

    expect(container.querySelector("circle")).toHaveAttribute("fill", "#7c3aed");
    expect(screen.getByText("GA")).toBeInTheDocument();
    expect(screen.getByText("Sân khấu")).toBeInTheDocument();
  });

  it("reports the clicked section through one handler on the svg", async () => {
    const user = userEvent.setup();
    const onSectionClick = vi.fn();
    const { container } = renderPreview([big], onSectionClick);

    await user.click(container.querySelectorAll("circle")[42]);

    expect(onSectionClick).toHaveBeenCalledWith("A");
  });
});

describe("presets", () => {
  it("are the three named layouts", () => {
    expect(PRESETS.map((p) => p.name)).toEqual(["Nhà hát 3 khu", "Sân khấu + khu đứng", "Sân vận động 4 phía"]);
  });

  it("fill sections with the first tier of the matching kind", () => {
    const sections = PRESETS.find((p) => p.id === "stage-standing")!.sections(tiers);

    expect(sections.filter((s) => s.kind === "seated").every((s) => s.tierId === "t1")).toBe(true);
    expect(sections.filter((s) => s.kind === "standing").every((s) => s.tierId === "t2")).toBe(true);
  });

  it("leave the tier empty when no tier of that kind exists", () => {
    const sections = PRESETS.find((p) => p.id === "stage-standing")!.sections([tiers[0]]);

    expect(sections.find((s) => s.kind === "standing")!.tierId).toBe("");
  });

  it("produce layouts the generator accepts when tiers exist", async () => {
    const { validateLayout } = await import("@/lib/organizer/seat-layout");
    for (const preset of PRESETS) {
      expect(validateLayout({ sections: preset.sections(tiers) }, { t1: "Seated", t2: "GeneralAdmission" }), preset.id).toEqual([]);
    }
  });
});
