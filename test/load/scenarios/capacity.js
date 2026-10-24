// Fixed request rate against the API, to find where latency leaves the target.
// One run is one rate; test/load/capacity.sh steps through several.
//   k6 run -e BASE_URL=http://localhost:8080 -e RATE=100 -e DURATION=30s scenarios/capacity.js
// The mix is what a reader does: mostly timeline pages (the first, and the next from a cursor),
// some feed lists and searches, and mark-as-read writes. Requests are issued at RATE per second
// whatever the server's speed, so when it falls behind latency and dropped iterations show it.
import http from "k6/http";
import { check } from "k6";
import { BASE_URL } from "../lib/config.js";

const RATE = Number(__ENV.RATE || 50);

export const options = {
  scenarios: {
    load: {
      executor: "constant-arrival-rate",
      rate: RATE,
      timeUnit: "1s",
      duration: __ENV.DURATION || "30s",
      preAllocatedVUs: Math.min(500, Math.max(20, RATE)),
      maxVUs: 1500,
    },
  },
  thresholds: {
    http_req_duration: ["p(95)<500"],
    http_req_failed: ["rate<0.01"],
  },
};

export function setup() {
  const res = http.get(`${BASE_URL}/api/item/timeline?pageSize=20`);
  const items = res.status === 200 ? JSON.parse(res.body) : [];
  const last = items[items.length - 1];
  return {
    cursor: last ? `&cursorPublishDateOrder=${last.publishDateOrder}&cursorId=${last.id}` : "",
    ids: items.map((i) => i.id),
  };
}

export default function (data) {
  const r = Math.random();
  let res;
  if (r < 0.55) {
    res = http.get(`${BASE_URL}/api/item/timeline?pageSize=20`);
  } else if (r < 0.75) {
    res = http.get(`${BASE_URL}/api/item/timeline?pageSize=20${data.cursor}`);
  } else if (r < 0.85) {
    res = http.get(`${BASE_URL}/api/feed`);
  } else if (r < 0.92) {
    res = http.get(`${BASE_URL}/api/item/search?query=news&page=0&pageSize=20`);
  } else if (data.ids.length > 0) {
    const id = data.ids[Math.floor(Math.random() * data.ids.length)];
    res = http.post(`${BASE_URL}/api/item/markAsRead?itemId=${id}&isRead=${Math.random() > 0.5}`, null);
  } else {
    res = http.get(`${BASE_URL}/api/healthz`);
  }
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
