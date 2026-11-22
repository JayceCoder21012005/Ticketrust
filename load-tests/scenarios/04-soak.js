// Scenario 04 (spec 17): moderate load for DURATION (default 30m). RATE buyers start per second (constant arrival rate); a share of
// them (ABANDON, default 0.5) leaves the hold to expire, the rest buy. soak-sampler.sh records memory, GC and the Redis hold keys
// every 30 s meanwhile; collect-results.mjs judges the trend (no growth) from its samples.
import exec from 'k6/execution';
import { buyers, guard, outcomes, purchase, summary, TREND_STATS } from '../lib/common.js';

const RATE = Number(__ENV.RATE || 2);

export const options = {
  summaryTrendStats: TREND_STATS,
  scenarios: {
    soak: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: __ENV.DURATION || '30m',
      preAllocatedVUs: Number(__ENV.VUS || 100),
      maxVUs: Number(__ENV.MAX_VUS || 300),
    },
  },
  thresholds: {
    unexpected_responses: ['count==0'],
  },
};

export function setup() {
  guard();
}

export default function () {
  const index = exec.scenario.iterationInTest;
  if (index >= buyers.length) return;
  outcomes.add(1, { step: 'final', result: purchase(index, { abandonHold: Number(__ENV.ABANDON || 0.5), hotRatio: 0, gaRatio: 0.4, pollSeconds: 10 }) });
}

export function handleSummary(data) {
  return summary('04-soak', data);
}
