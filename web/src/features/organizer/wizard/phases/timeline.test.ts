import { describe, expect, it } from "vitest";
import { layoutTimeline } from "./timeline";

const now = Date.parse("2026-11-04T03:00:00Z");
const start = "2026-11-14T03:00:00Z"; // 10 days after now
const at = (days: number) => new Date(now + days * 86_400_000).toISOString();

describe("layoutTimeline", () => {
  it("places phases on a bar from now to the event start", () => {
    const timeline = layoutTimeline([{ id: "a", opensAt: at(1), closesAt: at(6) }], now, start);

    expect(timeline.blocks).toEqual([{ id: "a", leftPct: 10, widthPct: 50, overlaps: false, tooSoon: false }]);
    expect(timeline.eventMarkPct).toBe(100);
  });

  it("flags both phases of an overlap", () => {
    const timeline = layoutTimeline(
      [
        { id: "a", opensAt: at(1), closesAt: at(4) },
        { id: "b", opensAt: at(3), closesAt: at(6) },
        { id: "c", opensAt: at(6), closesAt: at(8) },
      ],
      now,
      start,
    );

    expect(timeline.blocks.map((b) => [b.id, b.overlaps])).toEqual([
      ["a", true],
      ["b", true],
      ["c", false],
    ]);
  });

  it("warns when a phase opens less than 10 minutes from now", () => {
    const blocks = layoutTimeline(
      [
        { id: "soon", opensAt: new Date(now + 9 * 60_000).toISOString(), closesAt: at(1) },
        { id: "ok", opensAt: new Date(now + 10 * 60_000).toISOString(), closesAt: at(2) },
      ],
      now,
      start,
    ).blocks;

    expect(blocks.find((b) => b.id === "soon")!.tooSoon).toBe(true);
    expect(blocks.find((b) => b.id === "ok")!.tooSoon).toBe(false);
  });

  it("clamps phases outside the bar and ignores ones with unparsable times", () => {
    const blocks = layoutTimeline(
      [
        { id: "past", opensAt: at(-3), closesAt: at(2) },
        { id: "bad", opensAt: "", closesAt: at(2) },
      ],
      now,
      start,
    ).blocks;

    expect(blocks).toEqual([{ id: "past", leftPct: 0, widthPct: 20, overlaps: false, tooSoon: true }]);
  });
});
