// Sale scenario (spec 08 acceptance #8; a first cut of spec 17 "02-buy-flow" and "03-webhook-storm").
//
// One virtual buyer per iteration: login -> waiting room -> hold (seats and/or GA) -> POST /orders -> POST /orders/{id}/pay
// -> the mock gateway decides -> poll the order. Contention is on purpose: several buyers aim at the same few "hot" seats
// and the GA demand is larger than the tier's capacity. The database invariants are checked afterwards by
// load-tests/check-invariants.sh (see load-tests/README.md).
//
//   k6 run -e BASE_URL=http://localhost:8080 -e EVENT_ID=<uuid> -e GA_TIER_ID=<uuid> load-tests/sale.js
//
// Environment (all optional except EVENT_ID):
//   BASE_URL        api origin, default http://localhost:8080
//   EVENT_ID        the seated + GA event on sale
//   GA_TIER_ID      a general-admission tier of that event; without it nobody buys GA
//   BUYERS          iterations = distinct buyers, default 200 (users loadbuyerNNNNN@ticketrush.test, see sql/seed-buyers.sql)
//   VUS             concurrent buyers, default 50; clamped to BUYERS (shared-iterations needs iterations >= vus)
//   LOADTEST_CONFIRM  must be "loadtest" to run against any host other than localhost: this scenario creates real orders
//   BUYER_PASSWORD  default Dev@12345 (the dev seed's password, cloned by seed-buyers.sql)
//   SEAT_MIN, SEAT_MAX  seat index range of the event, default 0..599 (the seed's 600-seat map)
//   HOT_SEATS       how many of the first seats are "hot", default 10
//   HOT_RATIO       share of seated buyers that aim at a hot seat, default 0.6
//   GA_RATIO        share of buyers that buy GA instead of seats, default 0.3
//   GA_MAX_QTY      GA tickets per buyer, 1..N, default 2
//   MIX             gateway outcomes as succeeded:failed:delayed:twice:abandon weights, default 55:15:12:10:8
//   POLL_SECONDS    how long a buyer polls the order, default 25 (a "delayed" buyer is not waited for past this)
//   HOLD_P99_MS, CHECKOUT_P99_MS   thresholds, default 150 and 300 (the spec 17 budgets for a dedicated machine). On a laptop
//                   that also runs the stack, k6 and the database share the CPU: expect to raise them, e.g. -e HOLD_P99_MS=1000.
import http from 'k6/http';
import exec from 'k6/execution';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.2/index.js';

const BASE_URL = (__ENV.BASE_URL || 'http://localhost:8080').replace(/\/$/, '');
const API = `${BASE_URL}/api/v1`;
const EVENT_ID = __ENV.EVENT_ID;
const GA_TIER_ID = __ENV.GA_TIER_ID || '';
const BUYERS = Number(__ENV.BUYERS || 200);
const VUS = Math.min(Number(__ENV.VUS || 50), BUYERS);
const PASSWORD = __ENV.BUYER_PASSWORD || 'Dev@12345';
const SEAT_MIN = Number(__ENV.SEAT_MIN || 0);
const SEAT_MAX = Number(__ENV.SEAT_MAX || 599);
const HOT_SEATS = Number(__ENV.HOT_SEATS || 10);
const HOT_RATIO = Number(__ENV.HOT_RATIO || 0.6);
const GA_RATIO = GA_TIER_ID ? Number(__ENV.GA_RATIO || 0.3) : 0;
const GA_MAX_QTY = Number(__ENV.GA_MAX_QTY || 2);
const POLL_SECONDS = Number(__ENV.POLL_SECONDS || 25);
const MIX = (__ENV.MIX || '55:15:12:10:8').split(':').map(Number);
const OUTCOMES = ['succeeded', 'failed', 'succeeded_delayed', 'succeeded_twice', 'abandon'];

const TERMINAL = ['Paid', 'Refunded', 'RefundPending', 'Expired', 'Cancelled'];
const JSON_HEADERS = { 'Content-Type': 'application/json' };

// Anything that is not an expected business answer (a lost seat race is expected; a 5xx or a malformed body is not).
const unexpected = new Counter('sale_unexpected_responses');
const reachedPayment = new Rate('sale_reached_payment');
const results = {
  seats_contended: new Counter('sale_holds_seats_unavailable'),
  ga_insufficient: new Counter('sale_holds_ga_insufficient'),
  no_admission: new Counter('sale_not_admitted'),
  held: new Counter('sale_holds_created'),
  final: new Counter('sale_orders_final'),
  double_click_same_order: new Rate('sale_double_click_same_order'),
};
const holdTime = new Trend('sale_hold_duration', true);
const checkoutTime = new Trend('sale_checkout_duration', true);

export const options = {
  // The thresholds are on p(99); k6's default summary stops at p(95), so the number a threshold judged would never be printed.
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    buyers: { executor: 'shared-iterations', vus: VUS, iterations: BUYERS, maxDuration: '30m' },
  },
  thresholds: {
    sale_unexpected_responses: ['count==0'],
    sale_double_click_same_order: ['rate==1'],
    sale_hold_duration: [`p(99)<${Number(__ENV.HOLD_P99_MS || 150)}`],
    sale_checkout_duration: [`p(99)<${Number(__ENV.CHECKOUT_P99_MS || 300)}`],
  },
};

export function setup() {
  if (!EVENT_ID) {
    throw new Error('EVENT_ID is required (see load-tests/README.md)');
  }
  if (!/^https?:\/\/(localhost|127\.0\.0\.1)(:|\/|$)/.test(BASE_URL) && __ENV.LOADTEST_CONFIRM !== 'loadtest') {
    throw new Error(`${BASE_URL} is not localhost: set LOADTEST_CONFIRM=loadtest to confirm it is a load-test stack (this scenario creates real orders)`);
  }
  const probe = http.get(`${API}/events/${EVENT_ID}/holds/me`);
  // 401 is the healthy answer for an anonymous probe: it proves the api is up and the route exists.
  if (probe.status !== 401) {
    throw new Error(`api not reachable at ${BASE_URL} (probe answered ${probe.status})`);
  }
}

function buyerEmail(index) {
  return `loadbuyer${String(index + 1).padStart(5, '0')}@ticketrush.test`;
}

function authed(token, extra) {
  return { headers: Object.assign({ Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, extra || {}) };
}

function code(response) {
  try {
    return response.json('code');
  } catch (e) {
    return undefined;
  }
}

function pickOutcome() {
  const total = MIX.reduce((a, b) => a + b, 0);
  let roll = Math.random() * total;
  for (let i = 0; i < MIX.length; i++) {
    roll -= MIX[i];
    if (roll < 0) {
      return OUTCOMES[i];
    }
  }
  return OUTCOMES[0];
}

function randomInt(min, max) {
  return min + Math.floor(Math.random() * (max - min + 1));
}

function login(index) {
  const response = http.post(`${API}/auth/login`, JSON.stringify({ email: buyerEmail(index), password: PASSWORD }), {
    headers: JSON_HEADERS,
    tags: { name: 'login' },
  });
  if (response.status !== 200) {
    unexpected.add(1);
    return null;
  }
  return response.json('accessToken');
}

// Join the waiting room and poll as the server says until admitted. Returns the admission token or null.
function admit(token) {
  const join = http.post(`${API}/events/${EVENT_ID}/queue/join`, '{}', Object.assign(authed(token), { tags: { name: 'queue_join' } }));
  if (join.status !== 200) {
    unexpected.add(1);
    return null;
  }
  const deadline = Date.now() + 60000;
  let status = join.json();
  while (status.status !== 'Admitted') {
    if (['AllHeld', 'SoldOut', 'Closed', 'Expired'].includes(status.status) || Date.now() > deadline) {
      results.no_admission.add(1);
      return null;
    }
    sleep(Math.max(status.pollAfterMs || 1000, 250) / 1000);
    const poll = http.get(`${API}/events/${EVENT_ID}/queue/me`, Object.assign(authed(token), { tags: { name: 'queue_status' } }));
    if (poll.status !== 200) {
      unexpected.add(1);
      return null;
    }
    status = poll.json();
  }
  return status.admissionToken;
}

function selection() {
  if (Math.random() < GA_RATIO) {
    return { ga: [{ tierId: GA_TIER_ID, quantity: randomInt(1, GA_MAX_QTY) }] };
  }
  const count = randomInt(1, 2);
  const seats = new Set();
  while (seats.size < count) {
    const hot = Math.random() < HOT_RATIO;
    seats.add(hot ? SEAT_MIN + randomInt(0, HOT_SEATS - 1) : randomInt(SEAT_MIN + HOT_SEATS, SEAT_MAX));
  }
  return { seatIndexes: [...seats] };
}

// Creates the hold. A lost seat race is a normal answer: retry once on cold seats, then give up politely.
function hold(token, admission) {
  for (let attempt = 0; attempt < 2; attempt++) {
    const body = attempt === 0 ? selection() : { seatIndexes: [randomInt(SEAT_MIN + HOT_SEATS, SEAT_MAX)] };
    const response = http.post(
      `${API}/events/${EVENT_ID}/holds`,
      JSON.stringify(Object.assign({ replaceExisting: false }, body)),
      Object.assign(authed(token, { 'X-Admission-Token': admission, 'Idempotency-Key': uuidv4() }), { tags: { name: 'hold' } }),
    );
    holdTime.add(response.timings.duration);
    if (response.status === 200) {
      results.held.add(1);
      return response.json('holdId');
    }
    const reason = code(response);
    if (response.status === 409 && reason === 'hold.seats_unavailable') {
      results.seats_contended.add(1);
    } else if (response.status === 409 && reason === 'hold.insufficient_quantity') {
      results.ga_insufficient.add(1);
      return null;
    } else {
      unexpected.add(1);
      return null;
    }
  }
  return null;
}

// A hold or order that ran out between two steps is a normal business answer (the overlay's hold lasts 60 s).
const BUSINESS_REFUSALS = ['order.hold_expired', 'order.hold_invalid', 'order.expired', 'order.not_payable'];

// Classifies the two answers of a double click. Every answer must be a success, 409 idempotency.in_progress (the loser of the
// race), or, when nobody succeeded, one business refusal; anything else (5xx, 4xx, malformed) is unexpected.
// Returns the successful responses.
function settleDoubleClick(responses, okStatuses) {
  const usable = responses.filter((r) => okStatuses.includes(r.status));
  for (const r of responses) {
    if (okStatuses.includes(r.status)) {
      continue;
    }
    const reason = code(r);
    const inProgress = r.status === 409 && reason === 'idempotency.in_progress';
    const refusal = usable.length === 0 && r.status === 409 && BUSINESS_REFUSALS.includes(reason);
    if (!inProgress && !refusal) {
      unexpected.add(1);
    }
  }
  return usable;
}

// Two concurrent identical requests (a double click): both must answer with the same order.
function createOrder(token, holdId, index) {
  const key = uuidv4();
  const body = JSON.stringify({
    eventId: EVENT_ID,
    holdId,
    buyer: { name: `Load Buyer ${index + 1}`, email: buyerEmail(index), phone: '0901234567' },
  });
  const params = Object.assign(authed(token, { 'Idempotency-Key': key }), { tags: { name: 'order_create' } });
  const responses = http.batch([['POST', `${API}/orders`, body, params], ['POST', `${API}/orders`, body, params]]);
  checkoutTime.add(responses[0].timings.duration);
  const usable = settleDoubleClick(responses, [200, 201]);
  if (usable.length === 0) {
    return null;
  }
  results.double_click_same_order.add(usable.length === 1 || usable[0].json('id') === usable[1].json('id'));
  return usable[0].json('id');
}

function pay(token, orderId, key) {
  const params = Object.assign(authed(token, { 'Idempotency-Key': key }), { tags: { name: 'order_pay' } });
  const responses = http.batch([
    ['POST', `${API}/orders/${orderId}/pay`, null, params],
    ['POST', `${API}/orders/${orderId}/pay`, null, params],
  ]);
  checkoutTime.add(responses[0].timings.duration);
  const usable = settleDoubleClick(responses, [200]);
  if (usable.length === 0) {
    return null;
  }
  results.double_click_same_order.add(usable.length === 1 || usable[0].json('redirectUrl') === usable[1].json('redirectUrl'));
  const url = usable[0].json('redirectUrl');
  return url.substring(url.lastIndexOf('/') + 1);
}

function complete(sessionId, outcome) {
  const wire = outcome === 'abandon' ? null : outcome;
  if (!wire) {
    return;
  }
  const response = http.post(`${API}/mock-gateway/sessions/${sessionId}/complete`, JSON.stringify({ outcome: wire }), {
    headers: JSON_HEADERS,
    tags: { name: 'gateway_complete' },
  });
  // 409 = the session already expired or completed (a slow buyer): not a server fault.
  if (response.status !== 200 && response.status !== 409) {
    unexpected.add(1);
  }
}

function waitForOrder(token, orderId) {
  const deadline = Date.now() + POLL_SECONDS * 1000;
  let last = 'Unknown';
  while (Date.now() < deadline) {
    const response = http.get(`${API}/orders/${orderId}`, Object.assign(authed(token), { tags: { name: 'order_get' } }));
    if (response.status !== 200) {
      unexpected.add(1);
      return last;
    }
    last = response.json('status');
    if (TERMINAL.includes(last)) {
      return last;
    }
    sleep(1.5);
  }
  return last;
}

export default function () {
  const index = exec.scenario.iterationInTest;
  const token = login(index);
  const admission = token && admit(token);
  const holdId = admission && hold(token, admission);
  const orderId = holdId && createOrder(token, holdId, index);
  let sessionId = orderId && pay(token, orderId, uuidv4());
  if (!sessionId) {
    reachedPayment.add(false);
    return;
  }

  const outcome = pickOutcome();
  complete(sessionId, outcome);
  if (outcome === 'failed' && Math.random() < 0.5) {
    // Retry after a failure needs a FRESH Idempotency-Key (the same key replays the dead session's URL).
    sessionId = pay(token, orderId, uuidv4());
    if (sessionId) {
      complete(sessionId, 'succeeded');
    }
  }

  const final = waitForOrder(token, orderId);
  results.final.add(1, { status: final, outcome });
  reachedPayment.add(true);
}

export function handleSummary(data) {
  const path = __ENV.SUMMARY_PATH || 'load-tests/results/sale-summary.json';
  return {
    stdout: textSummary(data, { indent: ' ', enableColors: false }) + overview(data),
    [path]: JSON.stringify(data, null, 2),
  };
}

function overview(data) {
  const count = (name) => (data.metrics[name] ? data.metrics[name].values.count : 0);
  return [
    '',
    'TicketRush sale scenario',
    `  holds created           ${count('sale_holds_created')}`,
    `  lost seat races         ${count('sale_holds_seats_unavailable')}`,
    `  GA out of stock         ${count('sale_holds_ga_insufficient')}`,
    `  not admitted            ${count('sale_not_admitted')}`,
    `  unexpected responses    ${count('sale_unexpected_responses')}`,
    '  Now run the invariant check: load-tests/check-invariants.sh',
    '',
  ].join('\n');
}
