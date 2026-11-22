// Scenario 05 (spec 17): the buy flow of 02 while chaos.sh breaks things at fixed offsets: the worker leader is killed (compose
// restarts it), Redis restarts, Postgres is paused for 10 s. Errors during an outage are expected and only counted. After the
// chaos window a second wave of buyers (`after`) must buy with no unexpected answer: the system recovered. The invariants are
// checked afterwards, as after every scenario.
import exec from 'k6/execution';
import { buyers, guard, outcomes, purchase, summary, TREND_STATS } from '../lib/common.js';

const DURING = Number(__ENV.BUYERS || 1200);
const AFTER = Number(__ENV.AFTER_BUYERS || 100);
const CHAOS_SECONDS = Number(__ENV.CHAOS_SECONDS || 240);

export const options = {
  summaryTrendStats: TREND_STATS,
  scenarios: {
    during: { executor: 'shared-iterations', vus: Number(__ENV.VUS || 60), iterations: DURING, maxDuration: `${CHAOS_SECONDS}s`, exec: 'during' },
    after: { executor: 'shared-iterations', vus: 20, iterations: AFTER, startTime: `${CHAOS_SECONDS + 30}s`, maxDuration: '10m', exec: 'after' },
  },
  thresholds: {
    'unexpected_responses{scenario:after}': ['count==0'],
    'rate_limited_responses{scenario:after}': ['count==0'],
    double_click_disagreements: ['count==0'],
  },
};

export function setup() {
  guard();
}

export function during() {
  const index = exec.scenario.iterationInTest;
  outcomes.add(1, { step: 'final', result: purchase(index, { admissionTimeoutMs: 60000 }) });
}

export function after() {
  const index = DURING + exec.scenario.iterationInTest;
  if (index >= buyers.length) return;
  outcomes.add(1, { step: 'final', result: purchase(index) });
}

export function handleSummary(data) {
  return summary('05-chaos', data);
}
