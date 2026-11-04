import { describe, expect, it } from "vitest";
import type { OrganizerEvent, PublishChecklist } from "../types";
import { STEPS, stepLabel, stepStatus } from "./steps";

const draft = (patch: Partial<OrganizerEvent> = {}): OrganizerEvent => ({
  id: "e1",
  slug: "s",
  title: "Đêm nhạc",
  status: "Draft",
  category: "concert",
  venueId: "v1",
  startsAt: "2026-12-06T12:30:00Z",
  endsAt: "2026-12-06T15:30:00Z",
  description: "",
  lineup: [],
  coverImageUrl: null,
  tiers: [],
  layout: null,
  seatsPerTier: {},
  phases: [],
  version: "1",
  ...patch,
});

const checklist = (...steps: ("info" | "tiers" | "layout" | "phases")[]): PublishChecklist => ({
  ready: steps.length === 0,
  failures: steps.map((step) => ({ code: `x.${step}`, step, message: "" })),
});

describe("wizard steps", () => {
  it("are the five steps of the spec, in order, with Vietnamese labels", () => {
    expect(STEPS.map(stepLabel)).toEqual(["Thông tin", "Hạng vé", "Sơ đồ", "Mở bán", "Kiểm tra & Xuất bản"]);
  });

  it("marks a step with a failing rule as an error", () => {
    expect(stepStatus("info", checklist("info"), draft())).toBe("error");
  });

  it("marks steps without data and without failures as todo, and filled ones as done", () => {
    const c = checklist();
    expect(stepStatus("tiers", c, draft())).toBe("todo");
    expect(stepStatus("tiers", c, draft({ tiers: [{ id: "t", name: "VIP", price: 1, kind: "GeneralAdmission", capacity: 1, color: "#7c3aed" }] }))).toBe("done");
    expect(stepStatus("layout", c, draft({ layout: { sections: [] } }))).toBe("done");
    expect(stepStatus("info", c, draft({ coverImageUrl: "x" }))).toBe("done");
  });

  it("never marks a step of a published event as an error: its inventory is locked, so nothing could fix it", () => {
    expect(stepStatus("phases", checklist("phases"), draft({ status: "Published" }))).toBe("todo");
    expect(stepStatus("review", checklist("phases"), draft({ status: "Published" }))).toBe("done");
  });

  it("marks review as done once the checklist is ready", () => {
    expect(stepStatus("review", checklist(), draft())).toBe("done");
    expect(stepStatus("review", checklist("phases"), draft())).toBe("todo");
  });
});
