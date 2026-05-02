import http from "k6/http";
import { check, sleep } from "k6";
import { BASE_URL, THRESHOLDS } from "../lib/config.js";

export const options = {
  stages: [
    { duration: "1m", target: 25 },
    { duration: "2m", target: 50 },
    { duration: "2m", target: 50 },
    { duration: "1m", target: 0 },
  ],
  thresholds: THRESHOLDS,
};

export default function () {
  const rand = Math.random();

  if (rand < 0.6) {
    // 60% — timeline
    const res = http.get(
      `${BASE_URL}/api/item/timeline?page=0&pageSize=20`
    );
    check(res, {
      "timeline 200": (r) => r.status === 200,
    });
  } else if (rand < 0.9) {
    // 30% — feeds list
    const res = http.get(`${BASE_URL}/api/feed`);
    check(res, {
      "feeds 200": (r) => r.status === 200,
    });
  } else {
    // 10% — search
    const res = http.get(
      `${BASE_URL}/api/item/search?query=test&page=0&pageSize=20`
    );
    check(res, {
      "search 200": (r) => r.status === 200,
    });
  }

  sleep(0.5 + Math.random());
}
