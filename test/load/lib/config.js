// Load test configuration
// Backend runs with test auth enabled (testuser2)
export const BASE_URL = "https://localhost:9443";

export const THRESHOLDS = {
  http_req_duration: ["p(95)<2000"],
  http_req_failed: ["rate<0.05"],
};
