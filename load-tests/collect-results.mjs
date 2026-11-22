// Turns one run's raw output (load-tests/results) into the results.json the /lab page renders.
//   node load-tests/collect-results.mjs <results dir> <out file>
// Nothing here is invented: every number comes from a k6 summary, a sampler line or a checker verdict of this run. A scenario
// that did not run is listed with ran: false. The "what broke and how we fixed it" log is lab-notes.json, written by hand from
// what the runs actually showed.
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const [dir = 'load-tests/results', out = 'web/src/features/lab/results.json'] = process.argv.slice(2);
const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const read = (file, fallback = null) => {
  try {
    return JSON.parse(fs.readFileSync(path.join(dir, file), 'utf8'));
  } catch {
    return fallback;
  }
};
const lines = (file) => {
  try {
    return fs.readFileSync(path.join(dir, file), 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l));
  } catch {
    return [];
  }
};
const round = (n, d = 1) => (n === undefined || n === null || Number.isNaN(n) ? null : Math.round(n * 10 ** d) / 10 ** d);

const SCENARIOS = [
  { id: '01', file: '01-sale-open-spike', title: 'Sale-open spike', shape: 'Buyers join during the pre-queue, then poll as the server says', latency: [['queue_status_duration', 'Queue status']], budget: 'Queue status p99 < 50 ms, no error other than 429' },
  { id: '02', file: '02-buy-flow', title: 'Buy flow', shape: 'Admitted buyers: hold (30 % on the same hot seats) → order → mock pay, double clicks included', latency: [['hold_duration', 'Hold'], ['checkout_duration', 'Checkout']], budget: 'Hold p99 < 150 ms, checkout p99 < 300 ms' },
  { id: '03', file: '03-webhook-storm', title: 'Webhook storm', shape: 'Every payment webhook 3× in parallel, then 3× again after a random 0–5 min delay', latency: [['http_req_duration', 'All requests']], budget: 'Tickets issued once per order' },
  { id: '04', file: '04-soak', title: 'Soak', shape: 'Moderate constant load; half the holds are left to expire', latency: [['hold_duration', 'Hold'], ['checkout_duration', 'Checkout']], budget: 'Memory and GC stable, no growth in Redis hold keys' },
  { id: '05', file: '05-chaos', title: 'Chaos', shape: 'Buy flow while the worker leader is killed, Redis restarts and Postgres pauses 10 s', latency: [['hold_duration', 'Hold'], ['checkout_duration', 'Checkout']], budget: 'The system recovers and the invariants hold' },
];

function verdicts(file) {
  return (read(`${file}.invariants.json`, []) || []).map((v) => ({
    phase: v.phase,
    ok: v.exitCode === 0,
    violations: v.exitCode === 1 ? v.output.split('\n').filter((l) => l.includes(' | ') && !l.startsWith('check_name')) : [],
    note: v.exitCode === 2 ? 'the checker could not run' : null,
    // Redis = database, compared once settled only (null = not compared in this phase).
    redis: v.redisExitCode === undefined || v.redisExitCode < 0 ? null : { ok: v.redisExitCode === 0, detail: v.redisOutput.trim() },
  }));
}

function thresholds(metrics) {
  const found = [];
  for (const [name, metric] of Object.entries(metrics)) {
    for (const [rule, result] of Object.entries(metric.thresholds || {})) found.push({ metric: name, rule, ok: result.ok });
  }
  return found;
}

function slope(points) {
  if (points.length < 3) return null;
  const n = points.length;
  const mx = points.reduce((a, p) => a + p[0], 0) / n;
  const my = points.reduce((a, p) => a + p[1], 0) / n;
  const num = points.reduce((a, p) => a + (p[0] - mx) * (p[1] - my), 0);
  const den = points.reduce((a, p) => a + (p[0] - mx) ** 2, 0);
  return den === 0 ? 0 : num / den;
}

function soak() {
  const samples = lines('soak-samples.jsonl');
  if (samples.length === 0) return null;
  const tail = samples.filter((s) => s.t >= (samples.at(-1).t - 1200)); // the last 20 minutes
  const per10min = (key) => round((slope(tail.map((s) => [s.t, Number(s[key])]).filter((p) => Number.isFinite(p[1]))) ?? 0) * 600);
  const mean = (key) => tail.reduce((a, s) => a + Number(s[key] || 0), 0) / Math.max(tail.length, 1);
  const api = per10min('apiMiB');
  const worker = per10min('workerMiB');
  const holdKeysEnd = samples.at(-1).holdKeys;
  const stable = (s, key) => s !== null && Math.abs(s) * 2 <= 0.05 * mean(key); // < 5 % growth over the 20 minutes
  return {
    samples: samples.map((s) => ({ t: s.t, apiMiB: s.apiMiB, workerMiB: s.workerMiB, holdKeys: s.holdKeys, gcHeapMiB: s.gcHeapBytes ? round(s.gcHeapBytes / 1048576) : null })),
    apiMiBPer10Min: api,
    workerMiBPer10Min: worker,
    holdKeysPeak: Math.max(...samples.map((s) => s.holdKeys)),
    holdKeysEnd,
    memoryStable: stable(api, 'apiMiB') && stable(worker, 'workerMiB'),
    holdKeysBackToBaseline: holdKeysEnd <= 5,
  };
}

const commit = (() => {
  try {
    return execFileSync('git', ['rev-parse', '--short', 'HEAD'], { encoding: 'utf8' }).trim();
  } catch {
    return null;
  }
})();

const scenarios = SCENARIOS.map((s) => {
  const summary = read(`${s.file}.json`);
  if (!summary) return { id: s.id, title: s.title, shape: s.shape, budget: s.budget, ran: false };
  const m = summary.metrics;
  const value = (name, key) => m[name]?.values?.[key];
  return {
    id: s.id,
    title: s.title,
    shape: s.shape,
    budget: s.budget,
    ran: true,
    scale: { vusMax: value('vus_max', 'max') ?? null, iterations: value('iterations', 'count') ?? 0, seconds: round((summary.state?.testRunDurationMs ?? 0) / 1000, 0) },
    requests: value('http_reqs', 'count') ?? 0,
    rps: round(value('http_reqs', 'rate')),
    latency: s.latency
      .filter(([name]) => m[name])
      .map(([name, label]) => ({ label, p50: round(value(name, 'med')), p95: round(value(name, 'p(95)')), p99: round(value(name, 'p(99)')) })),
    errors: { unexpected: value('unexpected_responses', 'count') ?? 0, rateLimited: value('rate_limited_responses', 'count') ?? 0 },
    thresholds: thresholds(m),
    invariants: verdicts(s.file),
  };
});

const notes = (() => {
  try {
    return JSON.parse(fs.readFileSync(path.join(here, 'lab-notes.json'), 'utf8'));
  } catch {
    return { scaleNote: null, brokeAndFixed: [] };
  }
})();

const results = {
  generatedAt: new Date().toISOString(),
  commit,
  machine: read('machine.json'),
  scaleNote: notes.scaleNote,
  scenarios,
  soak: soak(),
  chaos: lines('chaos.jsonl'),
  brokeAndFixed: notes.brokeAndFixed,
};

fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify(results, null, 2) + '\n');
console.log(`results: ${out} (${scenarios.filter((s) => s.ran).length}/${scenarios.length} scenarios ran)`);
