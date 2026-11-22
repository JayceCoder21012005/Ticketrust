// Shared by the spec 17 scenarios: configuration and its safety guard, the seeded fixtures, the buyer's steps, and the summary file.
//
// Fixtures come from POST /api/v1/loadtest/seed (load-tests/run.sh calls it): FIXTURES_DIR holds seed.json (the event, small)
// and buyers.json (one pre-issued token per buyer). Tokens are loaded into a SharedArray, so 20,000 buyers cost one copy of the
// file, not one per virtual user.
import http from 'k6/http';
import { sleep } from 'k6';
import { SharedArray } from 'k6/data';
import { Counter, Trend } from 'k6/metrics';

export const BASE_URL = (__ENV.BASE_URL || 'http://127.0.0.1:8080').replace(/\/$/, '');
export const API = `${BASE_URL}/api/v1`;
const FIXTURES_DIR = (__ENV.FIXTURES_DIR || 'load-tests/results/fixtures').replace(/\\/g, '/').replace(/\/$/, '');
export const RESULTS_DIR = (__ENV.RESULTS_DIR || 'load-tests/results').replace(/\\/g, '/').replace(/\/$/, '');

// Turnstile's test token: the load-test overlay runs the fake CAPTCHA verifier, which accepts exactly this one.
export const CAPTCHA = 'XXXX.DUMMY.TOKEN.XXXX';

// open() resolves relative to this file, so the fixtures path must be absolute (run.sh passes one).
export const seed = JSON.parse(open(`${FIXTURES_DIR}/seed.json`));
export const buyers = new SharedArray('buyers', () => JSON.parse(open(`${FIXTURES_DIR}/buyers.json`)));

export const unexpected = new Counter('unexpected_responses');
export const rateLimited = new Counter('rate_limited_responses');
export const holdTime = new Trend('hold_duration', true);
export const checkoutTime = new Trend('checkout_duration', true);
export const queueStatusTime = new Trend('queue_status_duration', true);
export const outcomes = new Counter('purchase_outcomes');

/** Refuses anything but this machine unless the operator says it is a load-test stack: the scenarios create real orders. */
export function guard() {
  if (!/^https?:\/\/(localhost|127\.0\.0\.1)(:|\/|$)/.test(BASE_URL) && __ENV.LOADTEST_CONFIRM !== 'loadtest') {
    throw new Error(`${BASE_URL} is not localhost: set LOADTEST_CONFIRM=loadtest to confirm it is a load-test stack (this creates real orders)`);
  }
  const probe = http.get(`${API}/events/${seed.eventId}/holds/me`);
  if (probe.status !== 401) {
    throw new Error(`api not reachable at ${BASE_URL} (probe answered ${probe.status})`);
  }
}

const hex = (n) => {
  let s = '';
  for (let i = 0; i < n; i++) s += Math.floor(Math.random() * 16).toString(16);
  return s.replace(/^0+$/, '1'.padStart(n, '0'));
};

/** One W3C trace per purchase, like the web checkout sends (spec 17): its hold, order, payment and webhook share a trace. */
export const newTraceParent = () => `00-${hex(32)}-${hex(16)}-01`;

export function uuid() {
  const h = hex(32);
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-4${h.slice(13, 16)}-a${h.slice(17, 20)}-${h.slice(20, 32)}`;
}

export function authed(token, extra, name, trace) {
  const headers = Object.assign({ Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, extra || {});
  if (trace) headers.traceparent = trace;
  return { headers, tags: { name } };
}

export function code(response) {
  try {
    return response.json('code');
  } catch (e) {
    return undefined;
  }
}

/** A 429 is the system protecting itself (counted on its own); any other non-success outside `allowed` is unexpected. */
export function judge(response, okStatuses, allowed) {
  if (okStatuses.includes(response.status)) return true;
  if (response.status === 429) {
    rateLimited.add(1);
    return false;
  }
  if (!(allowed && allowed(response))) unexpected.add(1, { name: response.request ? response.tags.name || '' : '' });
  return false;
}

export const randomInt = (min, max) => min + Math.floor(Math.random() * (max - min + 1));

/** Joins the waiting room. Returns the first status, or null. */
export function join(token) {
  const response = http.post(`${API}/events/${seed.eventId}/queue/join`, JSON.stringify({ captchaToken: CAPTCHA }), authed(token, null, 'queue_join'));
  return judge(response, [200]) ? response.json() : null;
}

/** Polls `/queue/me` as the server says (pollAfterMs) until admitted. Returns the admission token, or null. */
export function waitForAdmission(token, first, timeoutMs) {
  const deadline = Date.now() + (timeoutMs || 120000);
  let status = first;
  while (status && status.status !== 'Admitted') {
    if (['AllHeld', 'SoldOut', 'Closed', 'Expired'].includes(status.status) || Date.now() > deadline) return null;
    sleep(Math.max(status.pollAfterMs || 1000, 250) / 1000);
    const poll = http.get(`${API}/events/${seed.eventId}/queue/me`, authed(token, null, 'queue_status'));
    queueStatusTime.add(poll.timings.duration);
    status = judge(poll, [200]) ? poll.json() : status;
    if (poll.status !== 200 && poll.status !== 429) return null;
  }
  return status ? status.admissionToken : null;
}

/** Seats or GA. `hotRatio` of seated buyers aim at the hot seats, so buyers fight over the same few. */
export function selection(options) {
  const o = Object.assign({ gaRatio: 0.3, hotRatio: 0.3, gaMaxQty: 2 }, options);
  if (seed.gaTierIds.length > 0 && (seed.seats === 0 || Math.random() < o.gaRatio)) {
    return { ga: [{ tierId: seed.gaTierIds[randomInt(0, seed.gaTierIds.length - 1)], quantity: randomInt(1, o.gaMaxQty) }] };
  }
  const count = randomInt(1, 2);
  const chosen = new Set();
  while (chosen.size < count) {
    const hot = seed.hotSeats > 0 && Math.random() < o.hotRatio;
    chosen.add(hot ? randomInt(0, seed.hotSeats - 1) : randomInt(seed.hotSeats, seed.seats - 1));
  }
  return { seatIndexes: [...chosen] };
}

/** Creates the hold; a lost seat race is retried once on a cold seat. Returns the hold id, or null. */
export function hold(token, admission, trace, options) {
  for (let attempt = 0; attempt < 2; attempt++) {
    const body = attempt === 0 || seed.seats === 0 ? selection(options) : { seatIndexes: [randomInt(seed.hotSeats, seed.seats - 1)] };
    const response = http.post(
      `${API}/events/${seed.eventId}/holds`,
      JSON.stringify(Object.assign({ replaceExisting: false }, body)),
      authed(token, { 'X-Admission-Token': admission, 'Idempotency-Key': uuid() }, 'hold', trace),
    );
    holdTime.add(response.timings.duration);
    if (response.status === 200 || response.status === 201) return response.json('holdId');
    const reason = code(response);
    if (response.status === 409 && reason === 'hold.seats_unavailable') {
      outcomes.add(1, { step: 'hold', result: 'seat_race_lost' });
      continue;
    }
    if (response.status === 409 && (reason === 'hold.insufficient_quantity' || reason === 'hold.limit_exceeded')) {
      outcomes.add(1, { step: 'hold', result: reason });
      return null;
    }
    judge(response, []);
    return null;
  }
  return null;
}

// A hold or order that ran out between two steps is a normal business answer (the overlay's hold lasts 60 s).
const BUSINESS_REFUSALS = ['order.hold_expired', 'order.hold_invalid', 'order.expired', 'order.not_payable'];

/** The two answers of a double click: successes, the loser's 409 in_progress, or (if nobody won) one business refusal. */
function settleDoubleClick(responses, okStatuses) {
  const usable = responses.filter((r) => okStatuses.includes(r.status));
  for (const r of responses) {
    if (okStatuses.includes(r.status)) continue;
    const reason = code(r);
    judge(r, okStatuses, () => (r.status === 409 && reason === 'idempotency.in_progress') || (usable.length === 0 && r.status === 409 && BUSINESS_REFUSALS.includes(reason)));
  }
  return usable;
}

export const doubleClickSame = new Counter('double_click_disagreements');

/** POST /orders twice at once with one key (a double click). Returns the order id, or null. */
export function createOrder(token, holdId, index, trace) {
  const body = JSON.stringify({ eventId: seed.eventId, holdId, buyer: { name: `Load Buyer ${index + 1}`, email: `buyer${index + 1}@loadtest.invalid`, phone: '0901234567' } });
  const params = authed(token, { 'Idempotency-Key': uuid() }, 'order_create', trace);
  const responses = http.batch([['POST', `${API}/orders`, body, params], ['POST', `${API}/orders`, body, params]]);
  checkoutTime.add(responses[0].timings.duration);
  const usable = settleDoubleClick(responses, [200, 201]);
  if (usable.length === 0) return null;
  if (usable.length === 2 && usable[0].json('id') !== usable[1].json('id')) doubleClickSame.add(1);
  return usable[0].json('id');
}

/** POST /orders/{id}/pay twice at once. Returns the mock session id, or null. */
export function pay(token, orderId, trace) {
  const params = authed(token, { 'Idempotency-Key': uuid() }, 'order_pay', trace);
  const responses = http.batch([['POST', `${API}/orders/${orderId}/pay`, null, params], ['POST', `${API}/orders/${orderId}/pay`, null, params]]);
  checkoutTime.add(responses[0].timings.duration);
  const usable = settleDoubleClick(responses, [200]);
  if (usable.length === 0) return null;
  if (usable.length === 2 && usable[0].json('redirectUrl') !== usable[1].json('redirectUrl')) doubleClickSame.add(1);
  const url = usable[0].json('redirectUrl');
  return url.substring(url.lastIndexOf('/') + 1);
}

/** The buyer's choice on the mock gateway page. 409 = the session already expired or completed (a slow buyer). */
export function complete(sessionId, outcome) {
  if (outcome === 'abandon') return;
  const response = http.post(`${API}/mock-gateway/sessions/${sessionId}/complete`, JSON.stringify({ outcome }), {
    headers: { 'Content-Type': 'application/json' },
    tags: { name: 'gateway_complete' },
  });
  judge(response, [200], (r) => r.status === 409);
}

const TERMINAL = ['Paid', 'Refunded', 'RefundPending', 'Expired', 'Cancelled'];

export function waitForOrder(token, orderId, seconds) {
  const deadline = Date.now() + (seconds || 25) * 1000;
  let last = 'Unknown';
  while (Date.now() < deadline) {
    const response = http.get(`${API}/orders/${orderId}`, authed(token, null, 'order_get'));
    if (!judge(response, [200])) return last;
    last = response.json('status');
    if (TERMINAL.includes(last)) return last;
    sleep(1.5);
  }
  return last;
}

export function pickOutcome(mix) {
  const names = ['succeeded', 'failed', 'succeeded_delayed', 'succeeded_twice', 'abandon'];
  const weights = (mix || '55:15:12:10:8').split(':').map(Number);
  let roll = Math.random() * weights.reduce((a, b) => a + b, 0);
  for (let i = 0; i < weights.length; i++) {
    roll -= weights[i];
    if (roll < 0) return names[i];
  }
  return names[0];
}

/** One whole purchase (scenario 02, the soak and chaos). Returns the final order status, or a step name it stopped at. */
export function purchase(index, options) {
  const o = Object.assign({ mix: __ENV.MIX, pollSeconds: Number(__ENV.POLL_SECONDS || 25), admissionTimeoutMs: 120000 }, options);
  const buyer = buyers[index % buyers.length];
  const trace = newTraceParent();
  const first = join(buyer.token);
  const admission = first && waitForAdmission(buyer.token, first, o.admissionTimeoutMs);
  if (!admission) return 'not_admitted';
  const holdId = hold(buyer.token, admission, trace, o);
  if (!holdId) return 'no_hold';
  if (o.abandonHold && Math.random() < o.abandonHold) return 'hold_abandoned';
  const orderId = createOrder(buyer.token, holdId, index, trace);
  if (!orderId) return 'no_order';
  let session = pay(buyer.token, orderId, trace);
  if (!session) return 'no_session';
  const outcome = pickOutcome(o.mix);
  complete(session, outcome);
  if (outcome === 'failed' && Math.random() < 0.5) {
    // A retry after a failure needs a fresh Idempotency-Key (the same key would replay the dead session).
    session = pay(buyer.token, orderId, trace);
    if (session) complete(session, 'succeeded');
  }
  return waitForOrder(buyer.token, orderId, o.pollSeconds);
}

export const TREND_STATS = ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max', 'count'];

/** Writes `<RESULTS_DIR>/<name>.json` (the raw k6 summary) for collect-results.mjs, and a short text summary to stdout. */
export function summary(name, data) {
  const lines = [`\n${name}`];
  for (const [metric, value] of Object.entries(data.metrics)) {
    if (value.type === 'trend' && value.values['p(99)'] !== undefined) {
      lines.push(`  ${metric.padEnd(28)} p50 ${value.values.med.toFixed(1)} ms  p95 ${value.values['p(95)'].toFixed(1)} ms  p99 ${value.values['p(99)'].toFixed(1)} ms`);
    } else if (value.type === 'counter') {
      lines.push(`  ${metric.padEnd(28)} ${value.values.count}`);
    }
    if (value.thresholds) {
      for (const [threshold, result] of Object.entries(value.thresholds)) lines.push(`    ${result.ok ? 'PASS' : 'FAIL'} ${threshold}`);
    }
  }
  return { stdout: lines.join('\n') + '\n', [`${RESULTS_DIR}/${name}.json`]: JSON.stringify(data, null, 2) };
}
