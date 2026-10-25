#!/bin/sh
#
# Cold-start entrypoint.
#
# Writer (default): see the "Writer path" section below.
# Reader (APP_ROLE=reader): read-only replica following the Litestream replica. The reader
# Container App is off in main.bicep (enableReadReplica=false) and slated for removal
# (BLOB-DURABILITY-SPEC.md section 4).

set -e

APP_ROLE="${APP_ROLE:-writer}"
ACTIVE_DB="/tmp/storage.db"

# ---------------------------------------------------------------------------
# Reader path: unchanged. Reader requires Litestream follow-mode.
# ---------------------------------------------------------------------------
if [ "$APP_ROLE" = "reader" ]; then
    echo "Starting in READER mode (read-only replica with follow-mode restore)." >&2
    litestream restore -f -config /etc/litestream.yml "$ACTIVE_DB" &
    LITESTREAM_PID=$!

    elapsed=0
    while [ ! -f "$ACTIVE_DB" ] && [ $elapsed -lt 30 ]; do
        sleep 1
        elapsed=$((elapsed + 1))
    done

    if [ ! -f "$ACTIVE_DB" ]; then
        echo "ERROR: Litestream follow-mode restore timed out after 30s." >&2
        kill $LITESTREAM_PID 2>/dev/null || true
        exit 1
    fi

    echo "Database restored (${elapsed}s). Starting app under follow-mode replication." >&2
    exec dotnet Server.dll
fi

# ---------------------------------------------------------------------------
# Writer path.
# ---------------------------------------------------------------------------
# Durability is the Litestream blob replica. The app itself restores /tmp/storage.db
# from it at boot (WriterStartupGate in Program.cs): it has to answer the startup probe
# first, because Container Apps only sends the previous revision SIGTERM once this one is
# "ready", and the restore waits for the previous writer's final sync and its blob lease.
# The app also starts and stops `litestream replicate` as its own child, after the restore,
# so Litestream never sees the file replaced underneath it. Nothing is seeded here.
#
# exec: dotnet becomes PID 1 and receives the platform's SIGTERM directly, which is what
# triggers the final sync before exit.
echo "Starting writer (database restored from the Litestream replica by the app)." >&2
exec dotnet Server.dll
