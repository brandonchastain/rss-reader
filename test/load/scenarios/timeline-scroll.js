import http from "k6/http";
import { check, sleep } from "k6";
import { Trend, Counter } from "k6/metrics";
import { BASE_URL } from "../lib/config.js";

// Simulates real user behavior: scroll timeline, mark items read, refresh
const pageLoad = new Trend("page_load_ms");
const page0Load = new Trend("page0_load_ms");
const page1Load = new Trend("page1_load_ms");
const page2plus = new Trend("page2plus_load_ms");
const markReadLatency = new Trend("mark_read_ms");
const successfulLoads = new Counter("successful_loads");

export const options = {
  scenarios: {
    scrollers: {
      executor: "ramping-vus",
      startVUs: 0,
      stages: [
        { duration: "20s", target: 50 },
        { duration: "20s", target: 100 },
        { duration: "20s", target: 150 },
        { duration: "2m", target: 200 },
        { duration: "20s", target: 0 },
      ],
    },
    // Background write pressure (like real usage)
    refreshers: {
      executor: "constant-vus",
      vus: 3,
      duration: "3m20s",
      exec: "backgroundRefresh",
    },
  },
  thresholds: {
    page_load_ms: ["p(95)<500", "p(99)<1000"],
    mark_read_ms: ["p(95)<100", "p(99)<500"],
    http_req_failed: ["rate<0.10"],
  },
};

export default function () {
  // Simulate scrolling: page 0, 1, 2, 3, 4
  for (let page = 0; page < 5; page++) {
    const res = http.get(
      `${BASE_URL}/api/item/timeline?page=${page}&pageSize=20`
    );

    const ok = check(res, {
      "timeline 200": (r) => r.status === 200,
    });

    if (ok) {
      pageLoad.add(res.timings.duration);
      successfulLoads.add(1);

      if (page === 0) page0Load.add(res.timings.duration);
      else if (page === 1) page1Load.add(res.timings.duration);
      else page2plus.add(res.timings.duration);

      // Mark 1-2 random items as read per page (realistic behavior)
      try {
        const items = JSON.parse(res.body);
        if (items.length > 0) {
          const count = 1 + Math.floor(Math.random() * 2);
          for (let i = 0; i < count && i < items.length; i++) {
            const item = items[Math.floor(Math.random() * items.length)];
            if (item.id) {
              const mr = http.get(
                `${BASE_URL}/api/item/markAsRead?itemId=${item.id}&isRead=true`
              );
              if (mr.status === 200) {
                markReadLatency.add(mr.timings.duration);
              }
              check(mr, { "markRead 200": (r) => r.status === 200 });
            }
          }
        }
      } catch (_) {}
    }

    // Brief pause between pages (scroll speed)
    sleep(0.1 + Math.random() * 0.2);
  }

  // Pause before next full scroll-through
  sleep(0.5 + Math.random() * 1.0);
}

export function backgroundRefresh() {
  http.get(`${BASE_URL}/api/feed/refresh`);
  sleep(5 + Math.random() * 10);
}
