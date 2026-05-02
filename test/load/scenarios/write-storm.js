import http from "k6/http";
import { check, sleep } from "k6";
import { BASE_URL, THRESHOLDS } from "../lib/config.js";

export const options = {
  stages: [
    { duration: "30s", target: 10 },
    { duration: "2m", target: 20 },
    { duration: "2m", target: 20 },
    { duration: "30s", target: 0 },
  ],
  thresholds: {
    http_req_duration: ["p(95)<3000"],
    http_req_failed: ["rate<0.10"],
  },
};

function getTimelineItems() {
  const res = http.get(
    `${BASE_URL}/api/item/timeline?page=0&pageSize=20`
  );
  if (res.status === 200) {
    try {
      return JSON.parse(res.body);
    } catch (_) {}
  }
  return [];
}

export default function () {
  const rand = Math.random();

  if (rand < 0.5) {
    // 50% — mark as read
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
  } else if (rand < 0.8) {
    // 30% — trigger refresh
    const res = http.get(`${BASE_URL}/api/feed/refresh`);
    check(res, {
      "refresh ok": (r) => r.status === 200 || r.status === 202,
    });
  } else {
    // 20% — read timeline (to keep some read pressure)
    const res = http.get(
      `${BASE_URL}/api/item/timeline?page=0&pageSize=20`
    );
    check(res, { "timeline 200": (r) => r.status === 200 });
  }

  sleep(0.3 + Math.random() * 0.7);
}
