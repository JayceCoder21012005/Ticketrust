// Scenario 01 (spec 17): the sale opens. VUS buyers (spec: 20,000) arrive during the pre-queue, join the waiting room, then
// poll /queue/me exactly as the server says (pollAfterMs) until admitted. They do not buy: this measures the waiting room.
//
// Pass: queue status p99 < QUEUE_P99_MS (50), and no error other than 429.
// Seed with opensInSeconds > ARRIVAL_SECONDS so every join lands in the pre-queue (run.sh does).
import exec from 'k6/execution';
import { sleep } from 'k6';
import { buyers, guard, join, randomInt, summary, TREND_STATS, waitForAdmission } from '../lib/common.js';

const VUS = Math.min(Number(__ENV.VUS || 20000), buyers.length);
const ARRIVAL_SECONDS = Number(__ENV.ARRIVAL_SECONDS || 60);

export const options = {
  summaryTrendStats: TREND_STATS,
  scenarios: {
    spike: { executor: 'per-vu-iterations', vus: VUS, iterations: 1, maxDuration: __ENV.MAX_DURATION || '15m' },
  },
  thresholds: {
    queue_status_duration: [`p(99)<${Number(__ENV.QUEUE_P99_MS || 50)}`],
    unexpected_responses: ['count==0'],
  },
};

export function setup() {
  guard();
}

export default function () {
  const buyer = buyers[exec.vu.idInTest - 1];
  // Arrivals spread over the pre-queue, as people open the page at different moments.
  sleep(randomInt(0, ARRIVAL_SECONDS * 1000) / 1000);
  const first = join(buyer.token);
  waitForAdmission(buyer.token, first, Number(__ENV.ADMISSION_TIMEOUT_MS || 600000));
}

export function handleSummary(data) {
  return summary('01-sale-open-spike', data);
}
