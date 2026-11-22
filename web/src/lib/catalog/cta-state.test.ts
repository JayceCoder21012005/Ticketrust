import { describe, expect, it } from "vitest";
import { computeCtaState, PRE_QUEUE_WINDOW_MS, type CtaInput } from "./cta-state";
import type { SalePhase } from "./types";

const MIN = 60_000;
const opensAt = Date.parse("2026-12-01T13:00:00Z");

function phase(overrides: Partial<SalePhase> = {}): SalePhase {
  return {
    id: "p1",
    name: "Mở bán",
    opensAt: new Date(opensAt).toISOString(),
    closesAt: "2026-12-20T13:00:00Z",
    queueEnabled: true,
    maxTicketsPerUser: 4,
    requiresAccessCode: false,
    ...overrides,
  };
}

function input(overrides: Partial<CtaInput> = {}): CtaInput {
  return { status: "Published", endsAt: "2026-12-21T16:00:00Z", isSoldOut: false, phases: [phase()], ...overrides };
}

describe("computeCtaState", () => {
  it("is Upcoming more than 30 minutes before a queued sale, and changes at T-30", () => {
    const state = computeCtaState(input(), opensAt - 31 * MIN);

    expect(state).toMatchObject({ kind: "Upcoming", opensAt: new Date(opensAt).toISOString() });
    expect(state.nextChangeAt).toBe(opensAt - PRE_QUEUE_WINDOW_MS);
  });

  it("is PreQueue from T-30 until the sale opens", () => {
    expect(computeCtaState(input(), opensAt - 30 * MIN)).toMatchObject({ kind: "PreQueue", nextChangeAt: opensAt });
    expect(computeCtaState(input(), opensAt - 1)).toMatchObject({ kind: "PreQueue" });
  });

  it("without a queue there is no pre-queue: Upcoming until the sale opens", () => {
    const state = computeCtaState(input({ phases: [phase({ queueEnabled: false })] }), opensAt - 10 * MIN);

    expect(state).toMatchObject({ kind: "Upcoming", nextChangeAt: opensAt });
  });

  it("is OnSale while a phase is open, until it closes", () => {
    const state = computeCtaState(input(), opensAt);

    expect(state).toMatchObject({ kind: "OnSale", nextChangeAt: Date.parse("2026-12-20T13:00:00Z") });
    expect(state.phase?.id).toBe("p1");
  });

  it("looks at the next phase when an earlier one has closed", () => {
    const presale = phase({ id: "pre", opensAt: "2026-11-20T13:00:00Z", closesAt: "2026-11-21T13:00:00Z" });

    expect(computeCtaState(input({ phases: [presale, phase()] }), Date.parse("2026-11-25T00:00:00Z"))).toMatchObject({
      kind: "Upcoming",
      phase: { id: "p1" },
    });
  });

  it("is SoldOut when the event is flagged or every tier has nothing left", () => {
    expect(computeCtaState(input({ isSoldOut: true }), opensAt)).toMatchObject({ kind: "SoldOut" });
    expect(computeCtaState(input(), opensAt, [{ tierId: "a", remaining: 0 }, { tierId: "b", remaining: 0 }])).toMatchObject({
      kind: "SoldOut",
    });
    expect(computeCtaState(input(), opensAt, [{ tierId: "a", remaining: 0 }, { tierId: "b", remaining: 3 }])).toMatchObject({
      kind: "OnSale",
    });
  });

  it("an empty tier list is not 'sold out'", () => {
    expect(computeCtaState(input(), opensAt, [])).toMatchObject({ kind: "OnSale" });
  });

  it("is Cancelled or Ended regardless of phases", () => {
    expect(computeCtaState(input({ status: "Cancelled" }), opensAt)).toMatchObject({ kind: "Cancelled" });
    expect(computeCtaState(input({ status: "Ended" }), opensAt)).toMatchObject({ kind: "Ended" });
    expect(computeCtaState(input(), Date.parse("2026-12-21T16:00:00Z"))).toMatchObject({ kind: "Ended" });
  });

  it("closes sales for an event an admin suspended, even inside an open phase", () => {
    expect(computeCtaState(input({ status: "Suspended" }), opensAt)).toEqual({ kind: "SalesClosed" });
  });

  it("is SalesClosed after the last phase and NotScheduled without phases", () => {
    expect(computeCtaState(input(), Date.parse("2026-12-20T13:00:00Z"))).toMatchObject({ kind: "SalesClosed" });
    expect(computeCtaState(input({ phases: [] }), opensAt)).toMatchObject({ kind: "NotScheduled" });
  });
});
