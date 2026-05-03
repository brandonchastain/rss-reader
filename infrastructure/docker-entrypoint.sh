#!/bin/sh

APP_ROLE="${APP_ROLE:-writer}"

if [ "$APP_ROLE" = "reader" ]; then
    # Reader mode: continuously restore WAL changes from blob storage.
    # litestream restore -f (follow mode) handles both the initial restore
    # AND continuous polling for new LTX files (~1s lag behind writer).
    # We skip the one-shot restore — follow mode manages the DB lifecycle
    # including the -txid sidecar for crash recovery.
    echo "Starting in READER mode (read-only replica with follow-mode restore)." >&2
    litestream restore -f -config /etc/litestream.yml /tmp/storage.db &
    LITESTREAM_PID=$!

    # Wait for the initial restore to produce the DB file
    echo "Waiting for Litestream initial restore..." >&2
    elapsed=0
    while [ ! -f /tmp/storage.db ] && [ $elapsed -lt 30 ]; do
        sleep 1
        elapsed=$((elapsed + 1))
    done

    if [ ! -f /tmp/storage.db ]; then
        echo "ERROR: Litestream follow-mode restore timed out after 30s." >&2
        kill $LITESTREAM_PID 2>/dev/null
        exit 1
    fi

    echo "Database restored (${elapsed}s). Starting app under follow-mode replication." >&2
    exec dotnet Server.dll
fi

# Writer mode: restore from Litestream if a replica exists, then start
# under Litestream's process supervision for continuous WAL replication.
#
# Litestream is now the SOLE backup path for the SQLite database — the previous
# secondary AzureFiles backup (DatabaseBackupService -> /data/storage.db) was
# removed. If `litestream restore` fails here, we MUST fail fast: starting the
# app on an empty DB would cause `litestream replicate` to mint a fresh
# generation against the empty file and orphan the existing replica
# (silent data loss).
if ! litestream restore -if-replica-exists -config /etc/litestream.yml /tmp/storage.db; then
    echo "FATAL: litestream restore failed. Refusing to start with empty DB to avoid orphaning the replica." >&2
    echo "       Investigate Litestream auth / blob connectivity before restarting." >&2
    exit 1
fi

# Litestream continuously replicates WAL changes to Blob Storage.
litestream replicate -exec "dotnet Server.dll" -config /etc/litestream.yml
exit_code=$?

# If `litestream replicate` itself exits, surface the error rather than running
# unreplicated — the app would write to /tmp/storage.db with no backup at all.
echo "FATAL: litestream replicate exited ($exit_code). Container will exit so it can be restarted with replication." >&2
exit $exit_code