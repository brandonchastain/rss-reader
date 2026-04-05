#!/bin/sh

# Restore database from Litestream if a replica exists in Blob Storage.
# On first boot (migration), this is a no-op  DatabaseBackupService will
# restore from Azure Files instead. On subsequent boots, Litestream has the
# latest data and restores it here.
litestream restore -if-replica-exists -config /etc/litestream.yml /tmp/storage.db || \
    echo "WARNING: Litestream restore failed. DatabaseBackupService will restore from Azure Files." >&2

# Start the app under Litestream's process supervision.
# Litestream continuously replicates WAL changes to Blob Storage.
# If Litestream fails (auth error, misconfiguration), fall back to running
# the app directly so DatabaseBackupService can still provide backup coverage.
litestream replicate -exec "dotnet Server.dll" -config /etc/litestream.yml
exit_code=$?

# If we reach here, Litestream exited. Fall back to running without replication.
echo "WARNING: Litestream replicate exited ($exit_code). Starting app without replication." >&2
exec dotnet Server.dll