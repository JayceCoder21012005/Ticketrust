import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { LabView } from "./lab-view";
import type { LabResults } from "./types";

const results: LabResults = {
  generatedAt: "2026-11-22T10:00:00Z",
  commit: "abc1234",
  machine: { cpu: "Test CPU", threads: 12, memoryGiB: 15.8, os: "Windows_NT 10", docker: { cpus: 12, memoryGiB: 7.6, version: "28.0" }, k6: "k6 v2.2.0", note: "One laptop runs everything." },
  scaleNote: null,
  scenarios: [
    {
      id: "02",
      title: "Buy flow",
      shape: "hold → order → pay",
      budget: "Hold p99 < 150 ms",
      ran: true,
      scale: { vusMax: 100, iterations: 2000, seconds: 300 },
      requests: 40000,
      rps: 133.3,
      latency: [{ label: "Hold", p50: 20, p95: 120, p99: 240.5 }],
      errors: { unexpected: 0, rateLimited: 3 },
      thresholds: [
        { metric: "hold_duration", rule: "p(99)<150", ok: false },
        { metric: "unexpected_responses", rule: "count==0", ok: true },
      ],
      invariants: [
        { phase: "after", ok: true, violations: [], note: null },
        { phase: "settled", ok: true, violations: [], note: null },
      ],
    },
    { id: "04", title: "Soak", shape: "steady", budget: "Stable", ran: false },
  ],
  soak: null,
  chaos: [{ at: 45, fault: "worker_leader_killed", detail: "abc" }],
  brokeAndFixed: [{ title: "Postgres ran out of connections", what: "53300 at 100 VUs", fix: "max_connections 300" }],
};

describe("LabView", () => {
  it("shows the measured numbers, a missed budget as missed, and every invariant check", () => {
    render(<LabView results={results} />);

    const rows = screen.getAllByRole("row");
    const buy = rows.find((r) => within(r).queryByText("02 · Buy flow"))!;
    expect(within(buy).getByText("Hold: 20 ms / 120 ms / 240,5 ms")).toBeInTheDocument();
    expect(within(buy).getByText("Không đạt")).toBeInTheDocument();
    expect(within(buy).getByText("hold_duration: p(99)<150")).toBeInTheDocument();
    expect(screen.getAllByLabelText("Đúng hết")).toHaveLength(2);
    expect(screen.getByText("Postgres ran out of connections")).toBeInTheDocument();
    expect(screen.getByText(/Tắt cưỡng bức worker đang làm leader/)).toBeInTheDocument();
  });

  it("marks a scenario that did not run instead of inventing numbers", () => {
    render(<LabView results={results} />);

    const soak = screen.getAllByRole("row").find((r) => within(r).queryByText("04 · Soak"))!;
    expect(within(soak).getByText("Chưa chạy")).toBeInTheDocument();
  });

  it("says when nothing has been recorded yet", () => {
    render(<LabView results={{ ...results, generatedAt: null, machine: null, scenarios: [], chaos: [], brokeAndFixed: [] }} />);

    expect(screen.getByText(/Chưa có lần chạy nào/)).toBeInTheDocument();
  });
});
