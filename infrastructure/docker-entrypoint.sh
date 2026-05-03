#!/bin/sh
#
# Cold-start entrypoint.
#
# Default mode (RSSREADER_ENABLE_LITESTREAM_REPLICATION != "true"):
#   Durability comes from the periodic file-mount backup service writing
#   /tmp/storage.db to /data/storage.db (Azure Files). On boot we just `cp`
#   /data → /tmp and exec dotnet. Cold start ≈ 5-15 sec regardless of DB size.
#
#   The migration path from the previous Litestream-only world is automatic:
#   if /data/storage.db is missing AND a Litestream blob replica exists, we
#   do a one-shot litestream restore and the backup service immediately
#   persists it to /data on its first cycle.
#
# Scaling mode (RSSREADER_ENABLE_LITESTREAM_REPLICATION == "true"):
#   Same /data seeding, but `litestream replicate` runs as the supervisor so
#   readers can stream WAL from blob. Each writer cold start mints a fresh
#   Litestream generation; readers must re-restore on generation change.
#
# See plan in session-state for full design.

set -e

APP_ROLE="${APP_ROLE:-writer}"
LITESTREAM_ENABLED="${RSSREADER_ENABLE_LITESTREAM_REPLICATION:-false}"
ACTIVE_DB="/tmp/storage.db"
# Backup path is REQUIRED in production (set via Bicep) and EMPTY in local dev.
# When empty, skip all seed/bootstrap logic — SQLite will create /tmp/storage.db
# fresh, which is the desired local-dev behavior.
BACKUP_DB="${RssAppConfig__BackupDbPath:-}"
BACKUP_DIR=""
if [ -n "$BACKUP_DB" ]; then
    BACKUP_DIR="$(dirname "$BACKUP_DB")"
fi

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
# Writer path: seed from the file-mount backup.
# ---------------------------------------------------------------------------

# Local-dev escape: if no BackupDbPath is configured, skip seeding/bootstrap
# entirely and let SQLite create /tmp/storage.db on first repository init.
if [ -z "$BACKUP_DB" ]; then
    echo "RssAppConfig__BackupDbPath is empty — skipping seed (local-dev mode)." >&2
    if [ "$LITESTREAM_ENABLED" = "true" ]; then
        echo "FATAL: RSSREADER_ENABLE_LITESTREAM_REPLICATION=true requires RssAppConfig__BackupDbPath." >&2
        exit 1
    fi
    echo "Starting in DEFAULT mode (no backup configured; ephemeral local DB)." >&2
    exec dotnet Server.dll
fi

# Validate the backup directory's grandparent (/data) exists and is writable.
# The /data Azure Files mount is the production durability surface; its absence
# means broken durability. The backup *subdir* may legitimately not exist on
# first boot — we mkdir it below.
PARENT_OF_BACKUP_DIR="$(dirname "$BACKUP_DIR")"
if [ ! -d "$PARENT_OF_BACKUP_DIR" ]; then
    echo "FATAL: Parent of backup directory '$PARENT_OF_BACKUP_DIR' does not exist." >&2
    echo "       In production this means the Azure Files mount is missing." >&2
    echo "       Check Container App volumeMounts configuration." >&2
    exit 1
fi
if [ ! -w "$PARENT_OF_BACKUP_DIR" ]; then
    echo "FATAL: Parent of backup directory '$PARENT_OF_BACKUP_DIR' is not writable." >&2
    exit 1
fi
mkdir -p "$BACKUP_DIR"

if [ -f "$BACKUP_DB" ]; then
    # Fast path: copy the file-mount backup into local /tmp.
    echo "Seeding $ACTIVE_DB from $BACKUP_DB..." >&2
    cp "$BACKUP_DB" "$ACTIVE_DB"
    SIZE=$(stat -c%s "$ACTIVE_DB" 2>/dev/null || echo "?")
    echo "Seeded $SIZE bytes from file-mount backup." >&2

    # Cheap integrity check on the copied seed. FATAL on failure — refusing to start
    # prevents the backup service from then overwriting /data with bad data.
    QC_RESULT=$(sqlite3 "$ACTIVE_DB" "PRAGMA quick_check;" 2>&1 || echo "FAILED")
    if [ "$QC_RESULT" != "ok" ]; then
        echo "FATAL: PRAGMA quick_check failed on seeded DB: $QC_RESULT" >&2
        echo "       Refusing to start; manual recovery required." >&2
        exit 1
    fi
    echo "quick_check on seeded DB: ok." >&2
else
    # No file-mount backup yet. Try the one-shot Litestream bootstrap path
    # (existing prod system has a blob replica from the previous architecture).
    echo "$BACKUP_DB not found — attempting one-shot Litestream restore." >&2
    if litestream restore -if-replica-exists -config /etc/litestream.yml "$ACTIVE_DB"; then
        if [ -f "$ACTIVE_DB" ]; then
            # Atomic bootstrap publish: copy to a unique temp path, validate,
            # then rename. A crash before rename leaves no /data/db/storage.db
            # so the next boot retries the litestream restore path.
            BOOTSTRAP_TMP="$BACKUP_DB.bootstrap.tmp.$$"
            trap 'rm -f "$BOOTSTRAP_TMP"' EXIT
            echo "One-shot Litestream restore succeeded; staging to $BOOTSTRAP_TMP." >&2
            cp "$ACTIVE_DB" "$BOOTSTRAP_TMP"
            BS_QC=$(sqlite3 "$BOOTSTRAP_TMP" "PRAGMA quick_check;" 2>&1 || echo "FAILED")
            if [ "$BS_QC" != "ok" ]; then
                echo "FATAL: quick_check failed on bootstrapped DB: $BS_QC" >&2
                rm -f "$BOOTSTRAP_TMP"
                exit 1
            fi
            mv -f "$BOOTSTRAP_TMP" "$BACKUP_DB"
            trap - EXIT
            echo "Bootstrapped $BACKUP_DB from Litestream replica." >&2
        else
            echo "FATAL: No /data backup and no Litestream replica found." >&2
            echo "       For brand-new environments, pre-create an empty DB at $BACKUP_DB." >&2
            exit 1
        fi
    else
        echo "FATAL: Litestream restore failed and no /data backup is present." >&2
        echo "       Investigate Litestream auth / blob connectivity." >&2
        exit 1
    fi
fi

# ---------------------------------------------------------------------------
# Default mode: file-mount backup is sole durability.
# ---------------------------------------------------------------------------
if [ "$LITESTREAM_ENABLED" != "true" ]; then
    echo "Starting in DEFAULT mode (file-mount backup; Litestream replication OFF)." >&2
    exec dotnet Server.dll
fi

# ---------------------------------------------------------------------------
# Scaling mode: Litestream replicates from the seeded DB. NOTE: this MINTS A
# NEW GENERATION in blob from /data state on every writer cold start. Readers
# must re-restore. Roll out per the sequenced procedure in the plan.
# ---------------------------------------------------------------------------
echo "Starting in SCALING mode (Litestream replication ON)." >&2
litestream replicate -exec "dotnet Server.dll" -config /etc/litestream.yml
exit_code=$?
echo "FATAL: litestream replicate exited ($exit_code). Container will exit." >&2
exit $exit_code
