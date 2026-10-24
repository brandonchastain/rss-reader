// The same fixed-rate idea against endpoints that need no sign-in (health and the anonymous
// session probe), used to compare a local container with the live app on the same footing.
import http from "k6/http";
import { check } from "k6";
import { BASE_URL } from "../lib/config.js";

const RATE = Number(__ENV.RATE || 20);

export const options = {
  scenarios: {
    load: {
      executor: "constant-arrival-rate",
      rate: RATE,
      timeUnit: "1s",
      duration: __ENV.DURATION || "20s",
      preAllocatedVUs: Math.min(200, Math.max(10, RATE)),
      maxVUs: 400,
    },
  },
};

export default function () {
  const res = http.get(Math.random() < 0.5 ? `${BASE_URL}/api/healthz` : `${BASE_URL}/auth/me`);
  check(res, { ok: (x) => x.status === 200 });
}

export function handleSummary(data) {
  const m = data.metrics;
  const v = (name, field) => (m[name] && m[name].values[field] !== undefined ? m[name].values[field] : 0);
  const line =
    `rate=${String(RATE).padStart(4)}/s  achieved=${v("http_reqs", "rate").toFixed(0).padStart(4)}/s  ` +
    `p50=${v("http_req_duration", "med").toFixed(0).padStart(5)} ms  p95=${v("http_req_duration", "p(95)").toFixed(0).padStart(5)} ms  ` +
    `p99=${v("http_req_duration", "p(99)").toFixed(0).padStart(5)} ms  failed=${(v("http_req_failed", "rate") * 100).toFixed(1).padStart(5)}%  ` +
    `dropped=${String(v("dropped_iterations", "count")).padStart(5)}
`;
  return { stdout: line };
}
