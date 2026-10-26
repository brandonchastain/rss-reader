#!/bin/sh
#
# Cold-start entrypoint.
#
# Durability is the Litestream blob replica. The app itself restores /tmp/storage.db
# from it at boot (WriterStartupGate in Program.cs): it has to answer the startup probe
# first, because Container Apps only sends the previous revision SIGTERM once this one is
# "ready", and the restore waits for the previous writer's final sync and its blob lease.
# The app also starts and stops `litestream replicate` as its own child, after the restore,
# so Litestream never sees the file replaced underneath it. Nothing is seeded here.
#
# exec: dotnet becomes PID 1 and receives the platform's SIGTERM directly, which is what
# triggers the final sync before exit.

set -e

echo "Starting writer (database restored from the Litestream replica by the app)." >&2
exec dotnet Server.dll
