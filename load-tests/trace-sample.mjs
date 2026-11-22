// Spec 17 acceptance: "a trace for one purchase shows hold → order → payment → webhook → outbox → email as connected spans".
//   node load-tests/trace-sample.mjs <trace.json from Tempo> > results/trace.json
// Reads one trace as Tempo's /api/traces/{id} returns it and keeps the purchase's steps in time order, with their service and
// duration, plus whether the trace holds every step (it is only written when it does: run.sh tries traces until one is whole).
import fs from 'node:fs';

const trace = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const spans = [];
for (const batch of trace.batches ?? trace.resourceSpans ?? []) {
  const service = batch.resource?.attributes?.find((a) => a.key === 'service.name')?.value?.stringValue ?? '?';
  for (const scope of batch.scopeSpans ?? batch.instrumentationLibrarySpans ?? []) {
    for (const span of scope.spans) {
      spans.push({ service, name: span.name, start: BigInt(span.startTimeUnixNano), end: BigInt(span.endTimeUnixNano), traceId: span.traceId });
    }
  }
}

const STEPS = [
  ['hold', /\/holds\/?$/],
  ['order', /^POST \/api\/v1\/orders\/?$/],
  ['payment', /\/pay$/],
  ['gateway delivery', /^mock-gateway deliver$/],
  ['webhook', /\/webhooks\/payments\//],
  ['outbox', /^outbox OrderPaid$/],
  ['email', /^email send /],
];

const origin = spans.reduce((min, s) => (s.start < min ? s.start : min), spans[0]?.start ?? 0n);
const steps = [];
for (const [step, pattern] of STEPS) {
  const span = spans.filter((s) => pattern.test(s.name)).sort((a, b) => (a.start < b.start ? -1 : 1))[0];
  if (span) {
    steps.push({ step, service: span.service, name: span.name, atMs: Number((span.start - origin) / 1000n) / 1000, durationMs: Number((span.end - span.start) / 1000n) / 1000 });
  }
}

const complete = steps.length === STEPS.length;
process.stdout.write(
  JSON.stringify({ complete, spanCount: spans.length, services: [...new Set(spans.map((s) => s.service))], steps }, null, 2),
);
process.exitCode = complete ? 0 : 1;
