// Load test configuration.
// BASE_URL is the API under test (the local container by default); run k6 with -e BASE_URL=... to change it.
// The API must run with the test user enabled so every request is authenticated.
export const BASE_URL = __ENV.BASE_URL || "http://localhost:8080";

export const THRESHOLDS = {
  http_req_duration: ["p(95)<2000"],
  http_req_failed: ["rate<0.05"],
};
