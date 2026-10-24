#!/usr/bin/env bash
# Steps through request rates against an API and prints one line per rate: where latency
# leaves the target (p95 under 500 ms, under 1% failures, nothing dropped) is the capacity.
#   test/load/capacity.sh                       # local container on :8080
#   BASE_URL=http://localhost:8080 RATES="25 50 100 200 400" DURATION=30s test/load/capacity.sh
#   SCENARIO=capacity-public BASE_URL=https://api.example.com RATES="10 20 40" test/load/capacity.sh
set -u
cd "$(dirname "$0")"
K6="${K6:-k6}"
SCENARIO="${SCENARIO:-capacity}"
BASE_URL="${BASE_URL:-http://localhost:8080}"
RATES="${RATES:-25 50 100 150 200 300 400}"
DURATION="${DURATION:-30s}"
echo "scenario=$SCENARIO target=$BASE_URL duration=$DURATION"
for rate in $RATES; do
  "$K6" run --quiet -e BASE_URL="$BASE_URL" -e RATE="$rate" -e DURATION="$DURATION" "scenarios/$SCENARIO.js" 2>/dev/null | grep "^rate="
  sleep 3
done
