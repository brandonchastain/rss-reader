import http from "k6/http";
import { check, sleep } from "k6";
import { BASE_URL } from "../lib/config.js";

// Aggressive write stress: 100 VUs, write-heavy, minimal sleep
// This is the scenario most likely to expose SQLite write contention
export const options = {
  stages: [
    { duration: "30s", target: 30 },
    { duration: "30s", target: 60 },
    { duration: "30s", target: 100 },
    { duration: "3m", target: 100 },
    { duration: "30s", target: 0 },
  ],
  thresholds: {
    http_req_duration: ["p(95)<10000"],
    http_req_failed: ["rate<0.15"],
  },
};

function getTimelineItems() {
  const res = http.get(
    `${BASE_URL}/api/item/timeline?page=0&pageSize=20`
  );
  if (res.status === 200) {
    try { return JSON.parse(res.body); } catch (_) {}
  }
  return [];
}

export default function () {
  const rand = Math.random();

  if (rand < 0.50) {
    // 50% — mark as read (high write pressure)
    const items = getTimelineItems();
    if (items.length > 0) {
      const item = items[Math.floor(Math.random() * items.length)];
      if (item.id) {
        const res = http.get(
          `${BASE_URL}/api/item/markAsRead?itemId=${item.id}&isRead=${Math.random() > 0.5}`
        );
        check(res, { "markRead 200": (r) => r.status === 200 });
      }
    }
  } else if (rand < 0.80) {
    // 30% — trigger refresh (heavy write — feed fetch + item insert)
    const res = http.get(`${BASE_URL}/api/feed/refresh`);
    check(res, {
      "refresh ok": (r) => r.status === 200 || r.status === 202,
    });
  } else {
    // 20% — timeline read (contention with writes)
    const page = Math.floor(Math.random() * 3);
    const res = http.get(
      `${BASE_URL}/api/item/timeline?page=${page}&pageSize=20`
    );
    check(res, { "timeline 200": (r) => r.status === 200 });
  }

  // Very short sleep — maximize write contention
  sleep(0.02 + Math.random() * 0.08);
}
