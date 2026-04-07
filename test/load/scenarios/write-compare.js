import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Trend } from "k6/metrics";
import { BASE_URL } from "../lib/config.js";

// Focused write comparison: stays under Kestrel's 50-connection cap
// but maximizes SQLite write pressure with minimal sleep
const writeLatency = new Trend("write_latency");
const readLatency = new Trend("read_latency");
const writeErrors = new Counter("write_errors");
const writeSuccess = new Counter("write_success");

export const options = {
  stages: [
    { duration: "20s", target: 20 },
    { duration: "20s", target: 40 },
    { duration: "3m", target: 40 },
    { duration: "20s", target: 0 },
  ],
  thresholds: {
    http_req_failed: ["rate<0.05"],
    write_latency: ["p(95)<5000"],
  },
};

function getTimelineItems() {
  const res = http.get(
    `${BASE_URL}/api/item/timeline?page=0&pageSize=20`
  );
  if (res.status === 200) {
    readLatency.add(res.timings.duration);
    try { return JSON.parse(res.body); } catch (_) {}
  }
  return [];
}

export default function () {
  const rand = Math.random();

  if (rand < 0.55) {
    // 55% — mark as read (single-row write)
    const items = getTimelineItems();
    if (items.length > 0) {
      const item = items[Math.floor(Math.random() * items.length)];
      if (item.id) {
        const res = http.get(
          `${BASE_URL}/api/item/markAsRead?itemId=${item.id}&isRead=${Math.random() > 0.5}`
        );
        writeLatency.add(res.timings.duration);
        if (res.status === 200) {
          writeSuccess.add(1);
        } else {
          writeErrors.add(1);
        }
        check(res, { "markRead 200": (r) => r.status === 200 });
      }
    }
  } else if (rand < 0.80) {
    // 25% — trigger refresh (bulk write — fetch feeds + insert items)
    const res = http.get(`${BASE_URL}/api/feed/refresh`);
    writeLatency.add(res.timings.duration);
    if (res.status === 200 || res.status === 202) {
      writeSuccess.add(1);
    } else {
      writeErrors.add(1);
    }
    check(res, {
      "refresh ok": (r) => r.status === 200 || r.status === 202,
    });
  } else {
    // 20% — timeline read (read under write pressure)
    const page = Math.floor(Math.random() * 5);
    const res = http.get(
      `${BASE_URL}/api/item/timeline?page=${page}&pageSize=20`
    );
    readLatency.add(res.timings.duration);
    check(res, { "timeline 200": (r) => r.status === 200 });
  }

  // Tight loop — maximize contention without hitting connection cap
  sleep(0.01 + Math.random() * 0.04);
}
