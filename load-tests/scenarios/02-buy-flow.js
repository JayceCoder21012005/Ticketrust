// Scenario 02 (spec 17): admitted buyers buy. Each iteration is one buyer: waiting room -> hold (random seats, HOT_RATIO of them
// aimed at the same hot seats, or GA) -> POST /orders and /pay each sent twice at once (a double click) -> the mock gateway
// decides (MIX) -> poll the order. Contention on purpose; the invariants are checked afterwards (check-invariants.sh).
//
// Pass: hold p99 < HOLD_P99_MS (150), checkout p99 < CHECKOUT_P99_MS (300), no unexpected answer, no double click answered twice.
import exec from 'k6/execution';
import { buyers, guard, outcomes, purchase, summary, TREND_STATS } from '../lib/common.js';

const BUYERS = Math.min(Number(__ENV.BUYERS || 2000), buyers.length);
const VUS = Math.min(Number(__ENV.VUS || 100), BUYERS);

export const options = {
  summaryTrendStats: TREND_STATS,
  scenarios: {
    buyers: { executor: 'shared-iterations', vus: VUS, iterations: BUYERS, maxDuration: __ENV.MAX_DURATION || '30m' },
  },
  thresholds: {
    hold_duration: [`p(99)<${Number(__ENV.HOLD_P99_MS || 150)}`],
    checkout_duration: [`p(99)<${Number(__ENV.CHECKOUT_P99_MS || 300)}`],
    unexpected_responses: ['count==0'],
    double_click_disagreements: ['count==0'],
  },
};

export function setup() {
  guard();
}

export function buy() {
  const index = exec.scenario.iterationInTest;
  outcomes.add(1, { step: 'final', result: purchase(index, { hotRatio: Number(__ENV.HOT_RATIO || 0.3), gaRatio: Number(__ENV.GA_RATIO || 0.3) }) });
}

export default buy;

export function handleSummary(data) {
  return summary(__ENV.SCENARIO_NAME || '02-buy-flow', data);
}
