#!/bin/sh

# Restore database from Litestream if a replica exists in Blob Storage.
# On first boot (migration), this is a no-op  DatabaseBackupService will
# restore from Azure Files instead. On subsequent boots, Litestream has the
# latest data and restores it here.
litestream restore -if-replica-exists -config /etc/litestream.yml /tmp/storage.db || \
    echo "WARNING: Litestream restore failed. DatabaseBackupService will restore from Azure Files." >&2

APP_ROLE="${APP_ROLE:-writer}"

if [ "$APP_ROLE" = "reader" ]; then
    # Reader mode: continuously restore WAL changes from blob storage.
    # litestream restore -f (follow mode) does the initial restore, then polls
    # for new LTX files every 1s to keep the DB ~1-2s behind the writer.
    echo "Starting in READER mode (read-only replica with follow-mode restore)." >&2
    litestream restore -f -config /etc/litestream.yml -o /tmp/storage.db &
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

# Writer mode: start the app under Litestream's process supervision.
# Litestream continuously replicates WAL changes to Blob Storage.
# If Litestream fails (auth error, misconfiguration), fall back to running
# the app directly so DatabaseBackupService can still provide backup coverage.
litestream replicate -exec "dotnet Server.dll" -config /etc/litestream.yml
exit_code=$?

# If we reach here, Litestream exited. Fall back to running without replication.
echo "WARNING: Litestream replicate exited ($exit_code). Starting app without replication." >&2
exec dotnet Server.dll