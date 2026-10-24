#!/usr/bin/env bash
# Request latency during a feed-refresh cycle (REPORT.md, "Refresh pacing"). Run from Git Bash with
# MSYS_NO_PATHCONV=1 and Docker Desktop running. Needs an image to measure and a database copy.
#   DB=../../dbcopy/storage.db test/load/refresh-pacing.sh prepare
#       one-time: Docker network 11.11.0.0/24, eight synthetic feed origins (lib/feedserver.py), a prepared
#       database (test user registered, 5,000 synthetic feeds subscribed, real feeds parked until 2027)
#   test/load/refresh-pacing.sh run <image> <tag> [extra docker -e args...]
#       starts the API capped at 0.5 vCPU / 1 GiB on a copy of the prepared database, runs capacity.js at
#       RATE (50) req/s for DURATION (60s) from the first scheduled fetch, then waits for the cycle to finish
#   test/load/refresh-pacing.sh teardown
set -u
cd "$(dirname "$0")"
LOAD="$PWD"
S="${WORK:-$PWD/results/refresh-pacing}"; mkdir -p "$S"
K6="${K6:-k6}"
DB="${DB:-../../dbcopy/storage.db}"
PREP_IMAGE="${PREP_IMAGE:-rss-reader-api:before}"
NET=rss-cap-net
ENV=(-e ASPNETCORE_ENVIRONMENT=Development -e RssAppConfig__IsTestUserEnabled=true
     -e RssAppConfig__FeedRefreshInterval=00:01:00 -e RssAppConfig__FeedRefreshIntervalFloor=00:01:00
     -e RssAppConfig__SchedulerTickInterval=00:01:00 -e RssAppConfig__SchedulerStartupDelay=00:00:05)

wait_healthy() {
  for i in $(seq 1 120); do
    curl -fsS http://localhost:8080/api/healthz >/dev/null 2>&1 && return 0
    sleep 1
  done
  echo "API never became healthy"; docker logs "$1" | tail -20; return 1
}

synthetic_done() { docker exec "$1" sqlite3 /tmp/storage.db "SELECT COUNT(*) FROM Feeds WHERE Url LIKE 'http://11.11.0.%' AND LastFetchedUtc IS NOT NULL;"; }

prepare() {
  docker network inspect $NET >/dev/null 2>&1 || docker network create --subnet 11.11.0.0/24 $NET
  for i in 0 1 2 3 4 5 6 7; do
    docker rm -f feed$i >/dev/null 2>&1
    docker run -d --name feed$i --network $NET --ip 11.11.0.1$i -v "$LOAD/lib/feedserver.py:/srv/feedserver.py:ro" python:3.12-slim python /srv/feedserver.py >/dev/null
  done
  docker run --rm --network $NET python:3.12-slim python -c "import urllib.request;print(len(urllib.request.urlopen('http://11.11.0.13/feed/1').read()),'bytes from a feed origin')"

  docker rm -f rss-prep >/dev/null 2>&1
  docker create --name rss-prep --network $NET -p 8080:8080 "${ENV[@]}" -e RssAppConfig__SchedulerEnabled=false "$PREP_IMAGE" >/dev/null
  docker cp "$DB" rss-prep:/tmp/storage.db
  docker start rss-prep >/dev/null
  wait_healthy rss-prep || return 1
  USER_JSON=$(curl -sS -X POST http://localhost:8080/api/user/register)
  echo "register: $USER_JSON"
  docker exec rss-prep sqlite3 /tmp/storage.db "
    CREATE TEMP TABLE seq(n INTEGER);
    WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM c WHERE n<5000) INSERT INTO seq SELECT n FROM c;
    INSERT INTO Feeds(Url, CreatedUtc) SELECT 'http://11.11.0.1'||(n%8)||'/feed/'||n, '2026-10-24T00:00:00.0000000+00:00' FROM seq;
    UPDATE Feeds SET NextFetchUtc='2027-01-01T00:00:00.0000000+00:00' WHERE Url NOT LIKE 'http://11.11.0.%';
    INSERT OR IGNORE INTO Subscriptions(UserId, FeedId, SubscribedUtc) SELECT (SELECT MAX(Id) FROM Users), Id, '2026-10-24T00:00:00.0000000+00:00' FROM Feeds;
    SELECT 'feeds', COUNT(*) FROM Feeds; SELECT 'synthetic', COUNT(*) FROM Feeds WHERE Url LIKE 'http://11.11.0.%';
    SELECT 'test user subscriptions', COUNT(*) FROM Subscriptions WHERE UserId=(SELECT MAX(Id) FROM Users);
    PRAGMA wal_checkpoint(TRUNCATE);"
  docker stop rss-prep >/dev/null
  docker cp rss-prep:/tmp/storage.db "$S/prepared.db"
  ls -la "$S/prepared.db"
}

run() {
  IMAGE=$1; TAG=$2; shift 2
  docker rm -f rss-cap >/dev/null 2>&1
  docker create --name rss-cap --network $NET --cpus 0.5 --memory 1g -p 8080:8080 "${ENV[@]}" "$@" "$IMAGE" >/dev/null
  docker cp "$S/prepared.db" rss-cap:/tmp/storage.db
  docker start rss-cap >/dev/null
  wait_healthy rss-cap || return 1
  curl -sS "http://localhost:8080/api/item/timeline?pageSize=1" | head -c 200; echo
  echo "[$TAG] waiting for the first scheduled fetch..."
  for i in $(seq 1 90); do [ "$(synthetic_done rss-cap)" -gt 0 ] && break; sleep 1; done
  T0=$(date +%s)
  sleep "${K6_START_DELAY:-0}"
  echo "[$TAG] synthetic fetched before k6: $(synthetic_done rss-cap)   cpu: $(docker stats --no-stream --format '{{.CPUPerc}}' rss-cap)"
  (cd "$LOAD" && "$K6" run --quiet -e BASE_URL=http://localhost:8080 -e RATE="${RATE:-50}" -e DURATION="${DURATION:-60s}" --summary-export "$S/k6-$TAG.json" scenarios/capacity.js 2>/dev/null | grep "^rate=" | sed "s/^/[$TAG] /")
  echo "[$TAG] synthetic fetched after k6: $(synthetic_done rss-cap)   cpu: $(docker stats --no-stream --format '{{.CPUPerc}}' rss-cap)"
  echo "[$TAG] waiting for the cycle to complete (5000 synthetic feeds fetched)..."
  while [ "$(synthetic_done rss-cap)" -lt 5000 ]; do sleep 2; done
  docker exec rss-cap sqlite3 /tmp/storage.db "SELECT 'cycle span (s)', CAST((julianday(MAX(LastFetchedUtc))-julianday(MIN(LastFetchedUtc)))*86400 AS INT) FROM Feeds WHERE Url LIKE 'http://11.11.0.%';" | sed "s/^/[$TAG] /"
  docker exec rss-cap sqlite3 /tmp/storage.db "SELECT 'synthetic posts', COUNT(*) FROM Posts p JOIN Feeds f ON f.Id=p.FeedId WHERE f.Url LIKE 'http://11.11.0.%';" 2>/dev/null
  docker logs rss-cap 2>&1 | grep -ci "error" | sed "s/^/[$TAG] log lines containing 'error': /"
}

teardown() {
  docker rm -f rss-cap rss-prep feed0 feed1 feed2 feed3 feed4 feed5 feed6 feed7 >/dev/null 2>&1
  docker network rm $NET >/dev/null 2>&1
  echo "removed the containers and the network; $S keeps prepared.db and the k6 summaries"
}

"$@"
