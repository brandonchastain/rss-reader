import http from "k6/http";
import { check, sleep } from "k6";
import { BASE_URL } from "../lib/config.js";

// Aggressive stress: 200 VUs, minimal sleep, tests read throughput ceiling
export const options = {
  stages: [
    { duration: "30s", target: 50 },
    { duration: "30s", target: 100 },
    { duration: "30s", target: 200 },
    { duration: "3m", target: 200 },
    { duration: "30s", target: 0 },
  ],
  thresholds: {
    http_req_duration: ["p(95)<5000"],
    http_req_failed: ["rate<0.05"],
  },
};

const SEARCH_TERMS = ["tech", "AI", "security", "open", "data", "cloud", "linux", "web", "app", "software"];

export default function () {
  const rand = Math.random();

  if (rand < 0.5) {
    // 50% — timeline with pagination
    const page = Math.floor(Math.random() * 5);
    const res = http.get(
      `${BASE_URL}/api/item/timeline?page=${page}&pageSize=20`
    );
    check(res, { "timeline 200": (r) => r.status === 200 });
  } else if (rand < 0.75) {
    // 25% — feeds list
    const res = http.get(`${BASE_URL}/api/feed`);
    check(res, { "feeds 200": (r) => r.status === 200 });
  } else {
    // 25% — search with varied terms
    const term = SEARCH_TERMS[Math.floor(Math.random() * SEARCH_TERMS.length)];
    const res = http.get(
      `${BASE_URL}/api/item/search?query=${term}&page=0&pageSize=20`
    );
    check(res, { "search 200": (r) => r.status === 200 });
  }

  // Minimal sleep — keep pressure high
  sleep(0.05 + Math.random() * 0.15);
}
