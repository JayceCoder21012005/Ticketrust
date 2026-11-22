/** What load-tests/collect-results.mjs writes (spec 17). Every number comes from a real run; `ran: false` = not measured. */
export type LabLatency = { label: string; p50: number | null; p95: number | null; p99: number | null };

export type LabInvariant = { phase: string; ok: boolean; violations: string[]; note: string | null };

export type LabScenario =
  | { id: string; title: string; shape: string; budget: string; ran: false }
  | {
      id: string;
      title: string;
      shape: string;
      budget: string;
      ran: true;
      scale: { vusMax: number | null; iterations: number; seconds: number | null };
      requests: number;
      rps: number | null;
      latency: LabLatency[];
      errors: { unexpected: number; rateLimited: number };
      thresholds: { metric: string; rule: string; ok: boolean }[];
      invariants: LabInvariant[];
    };

export type LabResults = {
  generatedAt: string | null;
  commit: string | null;
  machine: {
    cpu: string;
    threads: number;
    memoryGiB: number;
    os: string;
    docker: { cpus: number; memoryGiB: number; version: string } | null;
    k6: string | null;
    note: string;
  } | null;
  scaleNote: string | null;
  scenarios: LabScenario[];
  soak: {
    apiMiBPer10Min: number | null;
    workerMiBPer10Min: number | null;
    holdKeysPeak: number;
    holdKeysEnd: number;
    memoryStable: boolean;
    holdKeysBackToBaseline: boolean;
    samples: { t: number }[];
  } | null;
  chaos: { at: number; fault: string; detail: string }[];
  brokeAndFixed: { title: string; what: string; fix: string }[];
};
