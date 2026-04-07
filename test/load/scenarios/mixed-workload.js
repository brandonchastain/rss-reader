import http from "k6/http";
import { check, sleep } from "k6";
import { BASE_URL, THRESHOLDS } from "../lib/config.js";

export const options = {
  stages: [
    { duration: "1m", target: 15 },
    { duration: "2m", target: 30 },
    { duration: "2m", target: 30 },
    { duration: "1m", target: 0 },
  ],
  thresholds: THRESHOLDS,
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
    // 50% — timeline read
    const res = http.get(
      `${BASE_URL}/api/item/timeline?page=0&pageSize=20`
    );
    check(res, { "timeline 200": (r) => r.status === 200 });
  } else if (rand < 0.7) {
    // 20% — feeds list
    const res = http.get(`${BASE_URL}/api/feed`);
    check(res, { "feeds 200": (r) => r.status === 200 });
  } else if (rand < 0.9) {
    // 20% — mark as read (pick an item from timeline)
    const items = getTimelineItems();
    if (items.length > 0) {
      const item = items[Math.floor(Math.random() * items.length)];
      if (item.id) {
        const res = http.get(
          `${BASE_URL}/api/item/markAsRead?itemId=${item.id}&isRead=true`
        );
        check(res, { "markRead 200": (r) => r.status === 200 });
      }
    }
  } else if (rand < 0.95) {
    // 5% — trigger refresh
    const res = http.get(`${BASE_URL}/api/feed/refresh`);
    check(res, {
      "refresh ok": (r) => r.status === 200 || r.status === 202,
    });
  } else {
    // 5% — search
    const res = http.get(
      `${BASE_URL}/api/item/search?query=news&page=0&pageSize=20`
    );
    check(res, { "search 200": (r) => r.status === 200 });
  }

  sleep(0.5 + Math.random());
}
