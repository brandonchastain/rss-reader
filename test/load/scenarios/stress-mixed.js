import http from "k6/http";
import { check, sleep } from "k6";
import { BASE_URL } from "../lib/config.js";

// Aggressive stress: 150 VUs, mixed read/write, minimal sleep
export const options = {
  stages: [
    { duration: "30s", target: 50 },
    { duration: "30s", target: 100 },
    { duration: "30s", target: 150 },
    { duration: "3m", target: 150 },
    { duration: "30s", target: 0 },
  ],
  thresholds: {
    http_req_duration: ["p(95)<5000"],
    http_req_failed: ["rate<0.10"],
  },
};

const SEARCH_TERMS = ["tech", "AI", "security", "open", "data", "cloud", "linux", "web"];

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

  if (rand < 0.35) {
    // 35% — timeline with pagination
    const page = Math.floor(Math.random() * 5);
    const res = http.get(
      `${BASE_URL}/api/item/timeline?page=${page}&pageSize=20`
    );
    check(res, { "timeline 200": (r) => r.status === 200 });
  } else if (rand < 0.50) {
    // 15% — feeds list
    const res = http.get(`${BASE_URL}/api/feed`);
    check(res, { "feeds 200": (r) => r.status === 200 });
  } else if (rand < 0.75) {
    // 25% — mark as read (write)
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
  } else if (rand < 0.90) {
    // 15% — trigger refresh (heavy write)
    const res = http.get(`${BASE_URL}/api/feed/refresh`);
    check(res, {
      "refresh ok": (r) => r.status === 200 || r.status === 202,
    });
  } else {
    // 10% — search
    const term = SEARCH_TERMS[Math.floor(Math.random() * SEARCH_TERMS.length)];
    const res = http.get(
      `${BASE_URL}/api/item/search?query=${term}&page=0&pageSize=20`
    );
    check(res, { "search 200": (r) => r.status === 200 });
  }

  // Minimal sleep — keep pressure high
  sleep(0.05 + Math.random() * 0.15);
}
