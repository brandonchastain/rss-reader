import http from "k6/http";
import { check, sleep } from "k6";
import { BASE_URL, THRESHOLDS } from "../lib/config.js";

export const options = {
  stages: [
    { duration: "30s", target: 20 },
    { duration: "10m", target: 20 },
    { duration: "30s", target: 0 },
  ],
  thresholds: THRESHOLDS,
};

export default function () {
  const rand = Math.random();

  if (rand < 0.5) {
    const res = http.get(
      `${BASE_URL}/api/item/timeline?page=0&pageSize=20`
    );
    check(res, { "timeline 200": (r) => r.status === 200 });
  } else if (rand < 0.7) {
    const res = http.get(`${BASE_URL}/api/feed`);
    check(res, { "feeds 200": (r) => r.status === 200 });
  } else if (rand < 0.9) {
    // mark-as-read (fetch items first)
    const items = http.get(
      `${BASE_URL}/api/item/timeline?page=0&pageSize=5`
    );
    if (items.status === 200) {
      try {
        const parsed = JSON.parse(items.body);
        if (parsed.length > 0) {
          const item = parsed[Math.floor(Math.random() * parsed.length)];
          if (item.id) {
            const res = http.get(
              `${BASE_URL}/api/item/markAsRead?itemId=${item.id}&isRead=true`
            );
            check(res, { "markRead 200": (r) => r.status === 200 });
          }
        }
      } catch (_) {}
    }
  } else {
    const res = http.get(
      `${BASE_URL}/api/item/search?query=tech&page=0&pageSize=20`
    );
    check(res, { "search 200": (r) => r.status === 200 });
  }

  sleep(0.5 + Math.random());
}
