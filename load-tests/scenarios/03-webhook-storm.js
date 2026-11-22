// Scenario 03 (spec 17): webhook storm. Each buyer pays; the mock gateway sends its own webhook, and on top of it this script sends
// a second signed "payment.succeeded" for the same session 3x in parallel, then 3x again after a random 0..MAX_DELAY_SECONDS
// (default 300 = 5 min) delay. A gateway retries a webhook the receiver did not acknowledge, so a 500 "please deliver it again" is
// re-sent like a gateway would (up to 3 times) and counted on its own.
//
// Pass (checked afterwards by check-invariants.sh): tickets issued exactly once per order, whatever arrived how often.
// Here: no unexpected answer, and every storm delivery is acknowledged.
import http from 'k6/http';
import crypto from 'k6/crypto';
import exec from 'k6/execution';
import { sleep } from 'k6';
import { Counter } from 'k6/metrics';
import {
  API, buyers, complete, createOrder, guard, hold, join, judge, newTraceParent, outcomes, pay, randomInt, summary, TREND_STATS, uuid, waitForAdmission,
} from '../lib/common.js';

const BUYERS = Math.min(Number(__ENV.BUYERS || 500), buyers.length);
const VUS = Math.min(Number(__ENV.VUS || 100), BUYERS);
const SECRET = __ENV.WEBHOOK_SECRET;
const MAX_DELAY = Number(__ENV.MAX_DELAY_SECONDS || 300);

const sent = new Counter('storm_webhooks_sent');
const redelivered = new Counter('storm_webhooks_redelivered');
const lost = new Counter('storm_webhooks_unacknowledged');

export const options = {
  summaryTrendStats: TREND_STATS,
  scenarios: {
    storm: { executor: 'shared-iterations', vus: VUS, iterations: BUYERS, maxDuration: __ENV.MAX_DURATION || '30m' },
  },
  thresholds: {
    unexpected_responses: ['count==0'],
    storm_webhooks_unacknowledged: ['count==0'],
  },
};

export function setup() {
  if (!SECRET) throw new Error('WEBHOOK_SECRET is required (the stack\'s PAYMENT_WEBHOOK_SECRET)');
  guard();
}

/** X-Signature: t=<unix s>,v1=hex(HMAC-SHA256(secret, t + "." + body)), as the api verifies it. */
function signed(body) {
  const t = Math.floor(Date.now() / 1000);
  return { 'Content-Type': 'application/json', 'X-Signature': `t=${t},v1=${crypto.hmac('sha256', SECRET, `${t}.${body}`, 'hex')}` };
}

/** Sends the body `copies` times at once; a copy answered "not processed" is delivered again, like a gateway would. */
function storm(body, copies) {
  let pending = copies;
  for (let round = 0; round < 4 && pending > 0; round++) {
    const requests = [];
    for (let i = 0; i < pending; i++) requests.push(['POST', `${API}/webhooks/payments/mock`, body, { headers: signed(body), tags: { name: 'webhook' } }]);
    const responses = http.batch(requests);
    sent.add(responses.length);
    pending = 0;
    for (const r of responses) {
      if (r.status === 500) {
        pending++;
        continue;
      }
      judge(r, [200]);
    }
    if (pending > 0) {
      redelivered.add(pending);
      sleep(1);
    }
  }
  if (pending > 0) lost.add(pending);
}

export default function () {
  const index = exec.scenario.iterationInTest;
  const buyer = buyers[index];
  const trace = newTraceParent();
  const first = join(buyer.token);
  const admission = first && waitForAdmission(buyer.token, first);
  const holdId = admission && hold(buyer.token, admission, trace, { hotRatio: 0.2 });
  const orderId = holdId && createOrder(buyer.token, holdId, index, trace);
  const session = orderId && pay(buyer.token, orderId, trace);
  if (!session) {
    outcomes.add(1, { step: 'final', result: 'no_session' });
    return;
  }

  const view = http.get(`${API}/mock-gateway/sessions/${session}`, { tags: { name: 'gateway_session' } });
  if (!judge(view, [200])) return;
  complete(session, 'succeeded'); // the gateway's own webhook, delivered by the worker

  const body = JSON.stringify({
    id: `evt_storm_${uuid()}`,
    type: 'payment.succeeded',
    sessionId: session,
    amount: view.json('amount'),
    currency: view.json('currency'),
    occurredAt: new Date().toISOString(),
  });
  storm(body, 3);
  sleep(randomInt(0, MAX_DELAY * 1000) / 1000);
  storm(body, 3);
  outcomes.add(1, { step: 'final', result: 'stormed' });
}

export function handleSummary(data) {
  return summary('03-webhook-storm', data);
}
