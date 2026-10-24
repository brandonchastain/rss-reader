#!/usr/bin/env bash
#
# Durability integration test, BLOB-DURABILITY-SPEC.md 9.2. Runs the real image
# against Azurite (blob) and a shared volume (the share). Three scenarios:
#
#   1. handover  writer A gets SIGTERM while B waits in its gate; B serves every
#                row A acknowledged, the replica holds every row, A's final sync
#                and lease release are logged, B's lease acquire is logged.
#   2. crash     A gets SIGKILL; a restore of the replica passes quick_check and
#                is missing at most the rows acknowledged inside the last sync
#                interval.
#   3. overlap   B starts while A is alive: B answers 503 from its gate and runs
#                no Litestream; A is killed without a release; B proceeds only
#                after the lease duration.
#   4. cooperative  (RestoreFromBlob on) B asks on the handoff blob; A drains,
#                syncs and hands the lease over while alive; B restores through
#                `litestream restore -f`, serves every row, replicates on; A
#                answers 503 to writes and its SIGTERM skips the share copy.
#
# Rows are written straight into the live SQLite file with the sqlite3 CLI in
# the container (a probe table the app ignores): the property under test is the
# durability of the file, not the HTTP API, and the API cannot add posts without
# fetching a feed. Each acknowledged row is logged with its time so the crash
# scenario can bound the loss.
#
# Usage: RSS_IMAGE=<image> test/durability/run.sh [handover|crash|overlap]...
# Budget: about three minutes for all three on ubuntu-latest, image excluded.

set -euo pipefail
cd "$(dirname "$0")"
# Git Bash on Windows would rewrite /tmp/... container paths into Windows paths.
export MSYS_NO_PATHCONV=1

export RSS_IMAGE="${RSS_IMAGE:-rss-reader-api:durability}"
COMPOSE=(docker compose -f compose.yml --profile a --profile b)
NET="rss-durability_default"
DB=/tmp/storage.db
PORT_A=18081
PORT_B=18082
SYNC_INTERVAL_S=3        # test/durability/litestream.yml
LEASE_S=15               # compose.yml, the Azure minimum
ACK=$(mktemp)
WRITER_PID=""

log()  { printf '%s %s\n' "$(date -u +%H:%M:%S)" "$*"; }
fail() { log "FAIL: $*"; dump_logs; exit 1; }
pass() { log "PASS: $*"; }

dump_logs() {
  log "--- writer-a logs"; "${COMPOSE[@]}" logs --no-color writer-a 2>/dev/null | tail -60 || true
  log "--- writer-b logs"; "${COMPOSE[@]}" logs --no-color writer-b 2>/dev/null | tail -60 || true
}

cleanup() {
  stop_writer
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  rm -f "$ACK"
}
trap cleanup EXIT

reset() {
  stop_writer
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  : > "$ACK"
  "${COMPOSE[@]}" up -d azurite >/dev/null
  "${COMPOSE[@]}" create writer-a >/dev/null 2>&1   # creates the data volume under compose's name
  # A brand-new share: the entrypoint expects a database there (or a replica to
  # bootstrap from), as a new environment would be given one by hand.
  docker run --rm -v rss-durability_data:/data --entrypoint sh "$RSS_IMAGE" -c \
    'mkdir -p /data/db && sqlite3 /data/db/storage.db "CREATE TABLE IF NOT EXISTS Bootstrap (x); DROP TABLE Bootstrap;"'
}

sql() { docker exec "$1" sqlite3 "$DB" "$2"; }
count() { sql "$1" "SELECT count(*) FROM DurabilityProbe" 2>/dev/null || echo -1; }
healthz() { curl -s --max-time 3 "http://localhost:$1/api/healthz" || true; }
status_of() { curl -s -o /dev/null -w '%{http_code}' --max-time 3 "http://localhost:$1/api/item/timeline" || echo 000; }

# Waits until the real host answers: healthz 200 without the gate's "seeding".
wait_ready() {
  local port=$1 budget=${2:-120} i
  for ((i = 0; i < budget; i++)); do
    local body; body=$(healthz "$port")
    if [[ "$body" == *'"status":"healthy"'* && "$body" != *seeding* ]]; then return 0; fi
    sleep 1
  done
  return 1
}

# Writes a row every ~150 ms and records the time of each acknowledged write.
start_writer() {
  local c=$1
  sql "$c" "CREATE TABLE IF NOT EXISTS DurabilityProbe (n INTEGER PRIMARY KEY, t TEXT)"
  (
    while true; do
      if docker exec "$c" sqlite3 "$DB" "INSERT INTO DurabilityProbe (t) VALUES (strftime('%Y-%m-%dT%H:%M:%fZ','now'))" 2>/dev/null; then
        date -u +%s.%N >> "$ACK"
      fi
      sleep 0.15
    done
  ) &
  WRITER_PID=$!
}
stop_writer() {
  if [[ -n "$WRITER_PID" ]]; then kill "$WRITER_PID" 2>/dev/null || true; wait "$WRITER_PID" 2>/dev/null || true; WRITER_PID=""; fi
}

# Restores the replica from Azurite with the same Litestream binary and prints
# "<quick_check> <count>".
restore_replica() {
  docker run --rm --network "$NET" -v "$PWD/litestream.yml:/etc/litestream-test.yml:ro" --entrypoint sh "$RSS_IMAGE" -c '
    litestream restore -config /etc/litestream-test.yml -o /tmp/r.db /tmp/storage.db >/dev/null 2>&1 || { echo "restore-failed -1"; exit 0; }
    echo "$(sqlite3 /tmp/r.db "PRAGMA quick_check") $(sqlite3 /tmp/r.db "SELECT count(*) FROM DurabilityProbe" 2>/dev/null || echo -1)"'
}

logs_a() { "${COMPOSE[@]}" logs --no-color writer-a 2>/dev/null; }
logs_b() { "${COMPOSE[@]}" logs --no-color writer-b 2>/dev/null; }
# Log lines reach `compose logs` a moment after the event; allow up to 10 s.
expect_log() {
  local which=$1 pattern=$2 i
  for ((i = 0; i < 20; i++)); do
    if "logs_$which" | grep -q -- "$pattern"; then return 0; fi
    sleep 0.5
  done
  fail "writer-$which log lacks '$pattern'"
}
reject_log() { local which=$1 pattern=$2; if "logs_$which" | grep -q -- "$pattern"; then fail "writer-$which log contains '$pattern'"; fi; }

scenario_handover() {
  log "=== scenario 1: handover (SIGTERM)"
  reset
  "${COMPOSE[@]}" up -d writer-a >/dev/null
  wait_ready $PORT_A || fail "writer A did not become ready"
  expect_log a "Blob writer lease acquired"
  expect_log a "Litestream replicate started"
  start_writer rss-durability-writer-a-1
  sleep 6
  "${COMPOSE[@]}" up -d writer-b >/dev/null
  sleep 4
  [[ "$(healthz $PORT_B)" == *seeding* ]] || fail "writer B should be in its gate while A is alive"
  [[ "$(status_of $PORT_B)" == 503 ]] || fail "writer B's gate should answer 503"
  sleep 1
  stop_writer
  local n; n=$(count rss-durability-writer-a-1)
  log "A acknowledged $n rows; sending SIGTERM"
  "${COMPOSE[@]}" stop -t 60 writer-a >/dev/null
  expect_log a "Litestream final sync OK"
  expect_log a "Blob writer lease released"
  expect_log a "Shutdown: final DB backup OK"
  reject_log a "DISAGREE"
  wait_ready $PORT_B 120 || fail "writer B did not become ready after A stopped"
  local nb; nb=$(count rss-durability-writer-b-1)
  [[ "$nb" == "$n" ]] || fail "B serves $nb rows, A acknowledged $n"
  expect_log b "Blob writer lease acquired"
  reject_log b "DISAGREE"
  local r; r=$(restore_replica)
  log "replica restore: $r"
  [[ "$r" == "ok $n" ]] || fail "replica holds '$r', expected 'ok $n'"
  pass "handover: B serves all $n rows; replica holds all $n; lease and lock agree"
}

scenario_crash() {
  log "=== scenario 2: crash (SIGKILL)"
  reset
  "${COMPOSE[@]}" up -d writer-a >/dev/null
  wait_ready $PORT_A || fail "writer A did not become ready"
  start_writer rss-durability-writer-a-1
  sleep $((SYNC_INTERVAL_S * 4))
  local kill_at; kill_at=$(date -u +%s.%N)
  docker kill rss-durability-writer-a-1 >/dev/null
  stop_writer
  local n; n=$(wc -l < "$ACK")
  # Rows acknowledged more than one sync interval (plus a second of slack) before the kill must be in the replica.
  local must; must=$(awk -v k="$kill_at" -v s="$SYNC_INTERVAL_S" '$1 <= k - s - 1 {c++} END {print c+0}' "$ACK")
  local r; r=$(restore_replica)
  log "acknowledged $n rows, killed at +0; replica restore: $r; must hold at least $must"
  [[ "$r" == ok\ * ]] || fail "replica restore after crash: '$r'"
  local got=${r#ok }
  (( got >= must )) || fail "replica has $got rows, but $must were acknowledged more than one sync interval before the crash"
  (( got <= n )) || fail "replica has more rows ($got) than were acknowledged ($n)"
  pass "crash: replica quick_check ok, $got of $n rows, lost at most the last sync interval"
}

scenario_overlap() {
  log "=== scenario 3: overlap (B while A alive, then A dies without a release)"
  reset
  "${COMPOSE[@]}" up -d writer-a >/dev/null
  wait_ready $PORT_A || fail "writer A did not become ready"
  "${COMPOSE[@]}" up -d writer-b >/dev/null
  sleep 6
  [[ "$(healthz $PORT_B)" == *seeding* ]] || fail "writer B should be held in its gate"
  [[ "$(status_of $PORT_B)" == 503 ]] || fail "writer B's gate should answer 503"
  if docker exec rss-durability-writer-b-1 sh -c "grep -lq ^litestream /proc/[0-9]*/comm" 2>/dev/null; then fail "writer B started Litestream while A was alive"; fi
  expect_log b "Another writer holds the blob lease"
  local t0; t0=$(date +%s)
  docker kill rss-durability-writer-a-1 >/dev/null
  wait_ready $PORT_B 120 || fail "writer B never proceeded after A died"
  local waited=$(( $(date +%s) - t0 ))
  log "B proceeded $waited s after A was killed (lease $LEASE_S s)"
  (( waited >= LEASE_S - 3 )) || fail "B proceeded after $waited s, before the ${LEASE_S}s lease could expire"
  (( waited <= LEASE_S + 45 )) || fail "B took $waited s to proceed"
  expect_log b "Blob writer lease acquired"
  expect_log b "Stale writer lock found"
  docker exec rss-durability-writer-b-1 sh -c "grep -lq ^litestream /proc/[0-9]*/comm" 2>/dev/null || fail "writer B did not start Litestream after taking over"
  pass "overlap: B waited in the gate without replicating and proceeded ${waited}s after A died"
}

# Rollout step 3 (BLOB-DURABILITY-SPEC.md 3.2 v2): B asks on the handoff blob, A
# drains and hands the lease over while still alive, B restores from the replica
# through `litestream restore -f` and serves every row A acknowledged, A answers
# 503 to writes, and A's later SIGTERM skips the final share copy.
scenario_cooperative() {
  log "=== scenario 4: cooperative handover (restore from blob, marker before SIGTERM)"
  export RSS_RESTORE_FROM_BLOB=true
  reset
  "${COMPOSE[@]}" up -d writer-a >/dev/null
  wait_ready $PORT_A || fail "writer A did not become ready"
  expect_log a "Blob writer lease acquired"
  expect_log a "Handoff watcher running"
  start_writer rss-durability-writer-a-1
  sleep 6
  local t0; t0=$(date +%s)
  "${COMPOSE[@]}" up -d writer-b >/dev/null
  # A hands over on the marker, not on SIGTERM: it is never stopped before B is ready.
  expect_log b "Handover: requested"
  expect_log a "Handoff requested by"
  # The probe writer goes straight into the file, past the app's 503 on writes; stop
  # it where the middleware would start refusing, so its last rows precede the sync.
  stop_writer
  expect_log a "Handoff complete"
  local handed=$(( $(date +%s) - t0 ))
  local n; n=$(count rss-durability-writer-a-1)
  log "A handed over ${handed}s after B started; A acknowledged $n rows"
  [[ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 -X POST "http://localhost:$PORT_A/api/item/timeline")" == 503 ]] || fail "A should answer 503 to writes after the handoff"
  [[ "$(status_of $PORT_A)" != 503 ]] || fail "A should keep serving reads after the handoff"
  wait_ready $PORT_B 120 || fail "writer B did not become ready after the handoff"
  local ready=$(( $(date +%s) - t0 ))
  expect_log b "Handover: acknowledged by"
  expect_log b "Handover: restored"
  reject_log b "taking the full-restore path"
  reject_log b "seeding from the share copy"
  expect_log b "Blob writer lease acquired"
  local nb; nb=$(count rss-durability-writer-b-1)
  [[ "$nb" == "$n" ]] || fail "B serves $nb rows, A acknowledged $n"
  # B replicates on from the followed copy: new rows reach the same replica.
  start_writer rss-durability-writer-b-1
  sleep $((SYNC_INTERVAL_S * 3))
  stop_writer
  local n2; n2=$(count rss-durability-writer-b-1)
  docker exec rss-durability-writer-b-1 litestream sync -wait -timeout 20 -socket /tmp/litestream.sock "$DB" >/dev/null 2>&1 || fail "B's litestream sync failed"
  local r; r=$(restore_replica)
  log "B ready ${ready}s after start; B acknowledged $n2 rows; replica restore: $r"
  [[ "$r" == "ok $n2" ]] || fail "replica holds '$r', expected 'ok $n2'"
  # A's SIGTERM is now a no-op for the database.
  "${COMPOSE[@]}" stop -t 60 writer-a >/dev/null
  expect_log a "skipping the final share copy"
  expect_log a "already synced and stopped at the handoff"
  reject_log a "Shutdown: final DB backup OK"
  reject_log a "DISAGREE"
  unset RSS_RESTORE_FROM_BLOB
  pass "cooperative: A handed over in ${handed}s, B ready in ${ready}s with all $n rows, B replicates on, A's SIGTERM skipped the share copy"
}

scenarios=("$@")
[[ ${#scenarios[@]} -eq 0 ]] && scenarios=(handover crash overlap cooperative)
for s in "${scenarios[@]}"; do "scenario_$s"; done
log "all scenarios passed"
