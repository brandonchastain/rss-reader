# Durability on blob storage: Litestream replication, a blob lease, no file share

Status: chosen direction, 2026-10-24, pending the section 7 verifications.
Update 2026-10-25: rollout steps 1 to 5 are built. The key ring moved to blob
(`dataprotection/keys.xml`, imported once from the share) in #195; the share
backup, lock file, seed-from-share fallback, favicon write-through, volume and
mount are removed in the step 5 change. The replica is now the only durable
copy, and a restore that fails stops the container instead of falling back.
Not done: the reader app and `IsReadOnly` (section 4), deleting the share and
the old `storage.db/` replica prefix by hand, and `allowSharedKeyAccess=false`.
Considered against PR #170 (a block delta on shutdown over the existing share),
which fixes the handover but keeps the share, the storage key on the environment,
SMB under SQLite and a 15-minute crash recovery point; the owner chose this
design for the smaller long-term surface.
Scope: replace the whole-file backup on the Azure Files share with continuous
WAL replication to blob storage, replace the lock file with a blob lease,
move the two other tenants of the share (data-protection keys, cached favicons)
to blob, and remove the read-replica mode. The HTTP API, the schema and the
single-writer shape of the service do not change.

## 1. Why

Measured on the last ten production deploys (console logs, 2026-10-23/24):
the new revision's startup gate waits 59 to 60 s for the old writer's lock
every time, then seeds the database from the share in about 7 s. Users see
roughly 70 s of 503 responses per deploy, and almost all of it is the old
writer copying 322 MB to the share on shutdown.

The same copy sets the service's first hard limit. The final backup is capped
at `ShutdownBackupTimeout` (90 s) inside a 120 s termination grace period, and
a timed-out backup loses up to 15 minutes of writes. Backup time scales with
size, so the database is capped at about 450 MB (about 210,000 posts), which
at today's 180-day retention is reached around late December 2026
(`test/load/REPORT.md`, "Capacity at the live size").

Two smaller costs come with the share: Container Apps mounts Azure Files with
the storage account key, so rotating the key breaks the mount mid-backup (the
rotation runbook exists for this), and SMB does not give SQLite the locking it
needs, which is why backups are staged locally and renamed.

Litestream is already in the image (0.5.9 when this was written; 0.5.17 since 11.7) and in the infrastructure (a
`litestream` blob container, a blob role on the app's identity), but aimed at
read replicas that nothing routes to and that the capacity work shows are not
needed: the writer sustains 150 requests/s on 0.5 vCPU against a real peak of
0.14 requests/s. This spec points the same tool at the limit that does bind.

## 2. Goals and non-goals

Goals

1. Deploy handover in single-digit seconds, independent of database size.
2. Recovery point of 30 s or better at all times, including crashes.
3. No database size ceiling from the durability path.
4. One storage surface (blob), reached through managed identity, no key on
   the container.
5. Exactly one writer at any instant, enforced by something that expires on
   its own.

Non-goals

- Read replicas, routing, or any second serving instance. The reader role,
  its boot path and its Container App are removed.
- Point-in-time restore as a user feature. Litestream gives it for free; the
  operator may use it, the product does not.
- Changing the schema, the API, or the frontend.

## 3. Design

### 3.1 Components

| Component | Today | After |
|---|---|---|
| Live database | `/tmp/storage.db` on the container disk | unchanged |
| Durable copy | whole file at `/data/db/storage.db`, every 15 min and on shutdown | Litestream replica in blob container `litestream`: periodic snapshot plus WAL segments (LTX files), synced every 30 s |
| Writer lock | `/data/db/storage.db.lock`, mtime heartbeat, stale after 3 min | Azure Blob lease on blob `writer-lease`, 30 s duration, renewed every 10 s |
| Data-protection keys | `/data/keys` | ASP.NET blob key store, container `dataprotection` |
| Cached favicons | `/data/images` | dropped; they are a cache and are refetched. (Blob container `images` if refetch proves too slow; decide in step 4 of the rollout.) |
| Azure Files share `rss-data` | mounted at `/data` | removed after the rollout |
| Reader Container App, `APP_ROLE`, `IsReadOnly`, follow-mode restore | present, off | removed |

Process shape (changed after verification 11.3): the entrypoint execs
`dotnet Server.dll`, so the app is PID 1 and SIGTERM reaches it directly. The
app starts `litestream replicate` as its own child (`LitestreamSupervisor`)
once the database file is in place, and stops it in its own shutdown.
Litestream cannot supervise the app with `-exec`, because the file it opens is
replaced by a rename when the database is seeded or restored, and a daemon
started before that keeps the old inode.

### 3.2 Startup

The ordering constraint is unchanged from today and is the heart of the
design. Container Apps only stops the old revision once the new one answers
its startup probe, and the new one cannot open the database for writing until
the old one has stopped. So the new revision must answer the probe before it
has a database, and must wait for the old writer's release before it opens one.

```
entrypoint
  1. exec dotnet Server.dll
Server.dll
  2. Start the startup gate: /api/healthz -> 200 {phase: "restoring"},
     everything else -> 503 Retry-After: 5, CORS headers for allowed origins.
     (Existing WriterStartupGate, unchanged.)
  3. Acquire the blob lease on `writer-lease`, polling every 2 s.
     The old writer holds it until its final sync is done (3.3).
     A lease that was never released expires on its own after 30 s.
  4. litestream restore -o /tmp/storage.db <replica url>
     Full restore from the newest snapshot plus LTX tail. Measured (11.2):
     19 s at 322 MB in region on 0.5 vCPU, bound by decompression and page
     writes rather than by the network.
  5. PRAGMA quick_check on the restored file; refuse to start on failure
     (same as today).
  6. Stop the gate, start the real host. The app starts `litestream
     replicate` as its child here, so replication is attached from the first
     write. Its first sync re-snapshots the database (11.3); until that
     upload is done, new WAL segments queue behind it.
```

Users see 503 from the end of the old writer's drain to the end of step 6:
lease handoff (about 1 s), restore (19 s today, growing with size until the
v2 below), host start (about 2 s): about 25 s, against 70 s today.

Why restore after the lease rather than before it: a restore taken before
the old writer's final sync is missing that sync's writes, and applying only
the tail afterwards needs follow mode and a catch-up check. A full restore
after the lease is simpler and still removes the size dependence from the
handover, because the restore reads a snapshot from blob at network speed
rather than waiting on a 322 MB write over SMB. The measured restore time
(19 s, 11.2) does dominate the window, so v2 is the planned follow-up once
step 3 has shipped: `litestream restore -f` before the lease, then after the
lease wait until the local transaction id matches the one the old writer
recorded in the lease blob's metadata on release. That takes the window to
single digits independent of size; it is not needed for correctness.

### 3.3 Shutdown

```
SIGTERM (deploy, restart, scale event)
  1. Kestrel stops accepting, drains in-flight requests.
  2. Feed scheduler and background worker stop.
  3. PRAGMA wal_checkpoint(TRUNCATE) on the live database.
  4. `litestream sync -wait` (CLI, over the daemon's control socket, 11.4):
     forces an immediate upload of whatever WAL is pending and returns when
     it is done. Bounded by a 20 s timeout (`Durability:FinalSyncTimeout`).
     Then SIGTERM to the Litestream child, which runs its own final sync
     (`shutdown-sync-timeout`, 30 s) as a safety net and exits.
  5. Release the blob lease. Record the final LTX transaction id in the
     lease blob's metadata (used by v2 of startup, harmless otherwise).
  6. Process exits.
```

Step 4 replaces today's 55 s whole-file copy with an upload of at most 30 s
of WAL, typically kilobytes. `HostOptions.ShutdownTimeout` can drop from 110 s
to 30 s and `terminationGracePeriodSeconds` from 120 s to 60 s.

### 3.4 The lease

Azure Blob leases are acquired or refused atomically by the service and expire
server-side, so the three-minute stale threshold, the heartbeat file and the
"stale lock takeover" code path go away. Parameters:

| | Value | Reason |
|---|---|---|
| Duration | 30 s | Shortest that comfortably covers a renewal hiccup; the service allows 15 to 60 s |
| Renewal | every 10 s | Three attempts before expiry |
| Lost lease while running | log, keep serving, retry acquire | A new writer only exists during a deploy, and it acquires only after this one releases or expires. If this one cannot renew for 30 s and a new writer took over, this one is being terminated anyway |

The lease protects the replica, not the database file. Two writers on one
Litestream replica path corrupt the LTX chain; two writers on separate
container disks do not share a file at all. The lease is therefore acquired
before `litestream replicate` starts (step 3 before step 6) and released
only after `litestream sync` finishes.

### 3.5 Litestream configuration

The 0.5.9 keys (verified in 11.5; the first draft of this section used two
that 0.5.9 silently ignores). This is `infrastructure/litestream.yml`:

```yaml
socket:                       # control socket for `litestream sync -wait` (3.3)
  enabled: true
  path: /tmp/litestream.sock
snapshot:
  interval: 24h               # full snapshot cadence (the 0.5 default)
  retention: 72h              # how far back a point-in-time restore can go
levels:                       # LTX compaction, the defaults made explicit
  - interval: 30s
  - interval: 5m
  - interval: 1h
shutdown-sync-timeout: 30s    # the daemon's own final sync on SIGTERM
dbs:
  - path: /tmp/storage.db
    replica:
      type: abs
      account-name: ${LITESTREAM_AZURE_ACCOUNT_NAME}
      bucket: litestream
      path: storage.db
      sync-interval: 30s      # RPO; 1 s (the default) was the 2026-05 cost problem
```

Auth: the app's system-assigned identity already has Storage Blob Data
Contributor on the account, and Litestream 0.5.9's `abs` replica uses it
through the default Azure credential chain when no key or SAS is configured
(verified in 11.1). No key is on the container; the rotation runbook goes
away with the share at rollout step 5.

### 3.6 Data-protection keys and favicons

Keys: `PersistKeysToAzureBlobStorage` with the identity, container
`dataprotection`, blob `keys.xml`. Sessions survive deploys as they do today.
Rollout copies the current `keys.xml` from the share into the blob before the
switch, so no one is signed out.

Favicons: `/data/images` is restored into the container on boot by the
backup service today. Dropping it means the first request for each favicon
after a deploy fetches it from the site; there are 155 feeds. If that shows
in the UI, store them in an `images` container instead. Either way the
`DatabaseBackupService` image-restore path is deleted.

## 4. What is removed

- `DatabaseBackupToFileService`, `DatabaseSeeder`, `ShareWriterLock`,
  `WriterStartupGate.RunAsync`'s share-specific body (the gate itself stays),
  and their tests (about 11 cases, replaced by lease and restore tests).
- `docker-entrypoint.sh`: the reader path, the one-shot bootstrap restore,
  the "scaling mode" branch, the `/data` validation. What remains is: exec
  `litestream replicate -exec "dotnet Server.dll"`.
- Bicep: the reader Container App and its role assignment, `enableReadReplica`,
  `maxReadReplicas`, the Azure Files share, the environment storage, the
  volume and mount, `RssAppConfig__BackupDbPath`, `APP_ROLE`,
  `RSSREADER_ENABLE_LITESTREAM_REPLICATION`.
- Config: `IsReadOnly`, `BackupDbPath`, `BackupInterval`, `BackupLockStaleAfter`,
  `ShutdownBackupTimeout`. Added: `Durability:LeaseBlob`,
  `Durability:LeaseDuration`, `Durability:FinalSyncTimeout`.
- The `NoOpFeedRefresher` and `RepositoryFactory`'s read-only branch.

## 5. Failure modes

| Event | Today | After |
|---|---|---|
| Crash (OOM, node loss) | lose up to 15 min | lose up to 30 s |
| Deploy, normal | 70 s of 503, lose nothing | a few seconds of 503, lose nothing |
| Deploy, final sync times out (20 s) | n/a (final copy times out at 90 s, lose up to 15 min) | lose up to 30 s; new writer starts from the last synced position |
| Crash within the first minute or so after a deploy | lose up to 15 min | lose everything since the old writer's final sync: the new writer's first sync is a full snapshot (11.3) and its WAL segments queue behind that upload (tens of seconds in region at today's size) |
| Blob unreachable at startup | n/a (share unreachable: fatal) | gate keeps answering 200, restore retries with backoff for up to the probe budget (300 s), then fatal. Old revision keeps serving meanwhile because the new one never became "ready" in the app sense. Note the probe does see 200, so the platform will stop the old revision; this is the same exposure as today and is why the retry budget is short |
| Blob unreachable while running | n/a (share unreachable: backups fail, logged) | Litestream retries; WAL accumulates on the container disk (1 GiB limit, about 2 MB/day of growth, so days of headroom); lease renewal fails and is logged; the writer keeps serving |
| Two revisions overlap | lock file plus stale-takeover heuristics | lease; the second waits or the first's lease expires in 30 s |
| Corrupt restore | quick_check, fatal | quick_check, fatal; operator can `restore -timestamp` to an earlier point |
| Rollback to the previous image | works; same share format | works only if the previous image also uses the replica, so the rollout keeps the share path alive until step 5 |

## 6. Cost and targets

| | Today | After |
|---|---|---|
| Storage | 0.3 GB on Files at about $0.06/GB | 0.5 to 1 GB on blob hot at about $0.02/GB |
| Write operations | 96 copies/day of 322 MB over SMB | about 3,000 to 6,000 small puts/day (30 s syncs plus compaction), plus one full snapshot (about 230 MB) per deploy |
| Monthly, approximate list price | $1 to $2 | under $1 |
| Deploy 503 window | about 70 s, grows with size | about 25 s at today's size (19 s of it the restore, 11.2); under 10 s and size-independent with the v2 startup in 3.2 |
| RPO | 15 min | 30 s |
| Size ceiling from durability | about 450 MB | none |

Prices are approximate list prices for West US 2, LRS, hot tier; the point is
that both are noise next to the container, and that the earlier Litestream
cost was the 1 s default sync, about 15 times today's write count.

## 7. To verify before building

1. Does the 0.5.9 `abs` replica authenticate with managed identity? Try it in
   a container with the identity and no key; if it needs a key, see 3.5.
2. Full restore time of the current 322 MB database from blob, in region, at
   the default snapshot interval. Target under 10 s. Also measure at a 1 GB
   synthetic copy to confirm it scales linearly, not worse.
3. What `litestream replicate` does when started on a file freshly restored
   from the same replica: continue the LTX chain, or start a new one with a
   full snapshot upload? Either is acceptable; the restore in the next deploy
   must pick the newest, and the cost of a 322 MB snapshot per deploy is
   seconds and cents.
4. Does `litestream sync` (CLI) complete synchronously against the daemon, and
   does the daemon perform a final sync on child exit as well? Step 4 of 3.3
   depends on the first; the second is a safety net.
5. Confirm 0.5.9's config keys for retention and compaction before relying on
   the defaults named in 3.5.

Each of these is a one-hour experiment on a copy, and all five can run before
any production change.

## 8. Rollout

Every step leaves the previous durability path intact until the next step
proves the new one. Each has a rollback that is a redeploy of the previous
parameters.

1. **Shadow replication (one week).** Turn on `litestream replicate` in
   production alongside the existing file backup. Nothing restores from it
   yet. Measure: blob write count per day, replica size, and a daily timed
   restore of the replica to a scratch container with quick_check. Rollback:
   turn it off.
2. **Lease beside the lock.** The writer acquires the blob lease in addition
   to the lock file, logs both, releases both. Measure that the two never
   disagree across several deploys. Rollback: remove the lease code.
3. **Restore from blob at startup.** Startup restores from the replica
   instead of copying the share; the file backup still runs, so the share
   stays current and a rollback to the previous image still seeds correctly.
   Shutdown adds `litestream sync`. Measure the 503 window across deploys.
   Rollback: previous image, which reads the share that this step kept
   writing.
4. **Move keys and favicons; drop the lock file.** Copy `keys.xml` to blob,
   switch the key store, decide on favicons (3.6), remove the lock file and
   the stale-takeover code. Rollback: previous image plus copying keys back.
5. **Remove the file backup and the share.** Delete the backup service, the
   mount, the share, the reader app and the rest of section 4. Take a final
   share snapshot before deleting it and keep it for 30 days. Rollback from
   here is a restore from that snapshot into a redeploy of the step 3 image.

Shorten shutdown and grace timeouts (3.3) only at step 5, when nothing in the
shutdown path copies a whole file anymore.

## 9. Tests and CI

The property to protect is one sentence: after any sequence of deploys and
crashes, the new writer opens a database that passes quick_check and holds
every write the old writer acknowledged more than one sync interval before it
stopped, and at no instant do two writers replicate to the same path. The
layers below each cover part of it; together they cover all of it.

### 9.1 Unit tests (dotnet test, run in `ci.yml` and `deploy.yml` today)

Against Azurite, which supports blob leases, started as a service container in
the job:

- **Lease wrapper.** Acquire when free; refuse when held; renew keeps it;
  release frees it; expiry frees it after the duration with no release; a
  renew that fails for longer than the duration reports the lease lost.
- **Startup sequencer.** Order is acquire, restore, quick_check, open; the gate
  answers 200 on `/api/healthz` and 503 elsewhere until open; a restore failure
  retries with backoff and the gate stays up; a quick_check failure is fatal
  and releases the lease; a held lease makes it wait, not proceed.
- **Shutdown sequencer.** Order is drain, checkpoint, `litestream sync`,
  release; a sync that exceeds its timeout still releases the lease; a release
  failure is logged and does not block exit.
- **Litestream process wrapper.** The sync command's exit code and timeout
  are interpreted correctly, with the process faked.

These replace the eleven cases for `DatabaseBackupToFileService`,
`DatabaseSeeder` and `ShareWriterLock` that go with the share path.

### 9.2 Durability integration test (Docker, new job in `ci.yml`)

One script, run on every pull request, exercising the real binary against a
real replica. Azurite stands in for blob storage if Litestream's `abs` replica
accepts an endpoint override; otherwise the `file` replica type on a shared
Docker volume, which exercises the same restore and sync code and leaves only
the transport untested. Three scenarios:

1. **Handover.** Start writer A, write rows continuously, send SIGTERM, start
   writer B against the same replica, assert B serves every row A acknowledged,
   including one written in the last second before SIGTERM. This is sections
   3.2 and 3.3 end to end.
2. **Crash.** Same, with SIGKILL instead of SIGTERM. Assert quick_check passes
   and that the rows lost are only those acknowledged inside the last sync
   interval.
3. **Overlap.** Start B while A is alive. Assert B returns 503 from the gate
   and does not start replication. Kill A without a release. Assert B proceeds
   only after the lease duration, with the duration set to a few seconds for
   the test.

Budget: under five minutes on `ubuntu-latest`, so it can gate merges.

### 9.3 Browser end-to-end tests (existing, `ci.yml`)

The Playwright suite from PR #165 already runs the real stack in CI. It needs
no change beyond the compose file pointing at the replica instead of a bind
mount, and it continues to prove that a freshly started container serves a
working app.

### 9.4 Deploy-time verification (new steps in `deploy.yml`)

- **Handover window.** While the revision rolls, poll `/api/item/timeline` on
  the live hostname once a second and record the longest run of non-200
  answers. Report it in the job summary and fail the deploy if it exceeds
  30 s, which would mean the handover regressed to something like the old
  copy. Today's value would be about 70 s; the target is under 10.
- **Handover log check.** After the rollout, query Log Analytics for the old
  revision's "final sync OK" and the new revision's "restored N bytes" lines.
  Absence of either fails the job, since it means a path was skipped.

### 9.5 Restore check at cutover (by hand)

Decided 2026-10-24: not a scheduled workflow. The one that was written
(`restore-drill.yml`) was removed the same day, because for a single-writer
service whose only consumer of the replica is the next deploy, every deploy
is itself a restore and reports its own failure; a nightly job would add a
role assignment, runner minutes and an issue-opening path for a check that
is needed once. That once is the gate for rollout step 3: restore the
production replica to a scratch location with `litestream restore`, run
quick_check, and compare the counts of `Posts`, `Subscriptions`,
`UserPostState` and the maximum post id against the live `/api/healthz`
(which reports those numbers for this purpose). They must agree to within
one sync interval's worth of writes. Repeat after any change to the
Litestream config or binary.

### 9.6 What CI cannot cover

Azure's actual lease semantics, managed-identity auth for the replica, and
production restore time are measured in rollout steps 1 and 2 against the
real account, not in CI. The nightly drill then watches them for life.

## 10. Open decisions for the owner

- Retention of the replica (`retention: 72h` proposed). Longer costs storage
  only; it is the window for an operator point-in-time restore after a bad
  migration or a bug that deletes data.
- Favicons: drop and refetch, or an `images` blob container.
- Whether to take step 1 now, as a free measurement, before committing to the
  rest.

## 11. Verification results

Run 2026-10-24 against a copy of that morning's production database
(`dbcopy/storage-20261024.db`, 322,785,280 bytes, 151,618 posts) and a scratch
blob container `verify-scratch` in the production storage account. Nothing
touched the production database, the `litestream` container or the share.
In-region numbers come from a one-off Container Apps Job (`verify-litestream`,
0.5 vCPU, 1 GiB, same environment and image as the app, system-assigned
identity with Storage Blob Data Contributor on the account), deleted
afterwards. "From this machine" numbers are from a home connection and are
labelled as such.

### 11.1 Managed identity (section 7, item 1)

Result: **managed identity works; no key needed.** Litestream 0.5.9's `abs`
replica picks its credential in this order (`abs/replica_client.go`): a SAS
token (`sas-token` or `LITESTREAM_AZURE_SAS_TOKEN`), then a shared key
(`account-key` or `LITESTREAM_AZURE_ACCOUNT_KEY`), then
`azidentity.NewDefaultAzureCredential`, which in Container Apps resolves to the
system-assigned identity. With neither key nor SAS set, the job restored the
replica:

```
# in the job, image ghcr.io/brandonchastain/rss-reader-api:latest, no LITESTREAM_AZURE_ACCOUNT_KEY
litestream restore -o /tmp/restored.db abs://rssdatamoveqabxi4tro@verify-scratch/storage.db
# VERIFY restore rc=0 ms=19013
# VERIFY quick_check=ok
# VERIFY posts=151618
```

Two details found on the way:

- Litestream does not create the container; a missing one fails with
  `ContainerNotFound`. Bicep creates `litestream`, and `BlobWriterLease`
  calls `CreateIfNotExists` on its container before the first acquire, which
  covers Azurite in the tests.
- The `endpoint` replica key works against Azurite
  (`endpoint: http://azurite:10000/devstoreaccount1` with the well-known key),
  so CI uses Azurite for both the unit tests (9.1) and the Docker test (9.2),
  not the `file` replica fallback. Verified with `replicate -once` and
  `restore` round-tripping a small database.

Section 3.5's fallback (key as a Container Apps secret) is therefore not
needed. For the experiments from this machine, the storage account key was
read into a shell variable from `az storage account keys list` and passed to
the container as `LITESTREAM_AZURE_ACCOUNT_KEY`; it was never printed. A
user-delegation SAS from the developer's own `az login` did not work because
that user has no data-plane role on the account (403
`AuthorizationPermissionMismatch`); that is a fact about the account, not
about Litestream.

### 11.2 Restore time (section 7, item 2)

| Database | Replica snapshot (L0, LZ4) | Restore in region (0.5 vCPU) | Restore from this machine | quick_check |
|---|---|---|---|---|
| 322 MB production copy | 228,571,228 bytes | **19.0 s** | about 4 min (home downlink) | ok, 151,618 posts |
| 1.02 GB synthetic (the copy plus 700 MB of `randomblob` rows) | 935,481,959 bytes | under 70 s (see note) | not run | not retrieved (see note) |

Commands, in the job:

```
litestream restore -o /tmp/restored.db abs://rssdatamoveqabxi4tro@verify-scratch/storage.db     # 19,013 ms
# synthetic copy: restore, pad with 7,000 rows of randomblob(100000), replicate -once to storage-1g.db
litestream restore -o /tmp/r1g.db abs://rssdatamoveqabxi4tro@verify-scratch/storage-1g.db
```

Note on the 1 GB figure: the job that restored it (`VERIFY3`) ran for 84 s
wall clock from container start to exit and reported success, so the
restore itself took under 70 s. Its quick_check line and the exact
millisecond figure were in its console output, which Log Analytics had not
ingested when this section was written; both are in the workspace rather
than here. At most 3.6 times the 322 MB figure for 3.2 times the bytes:
linear, as section 7 asked.

The 322 MB number is above section 3.2's "under 10 s" target and below the
30 s that section 7 names as the point at which the design stops working.
Consequences are applied in sections 3.2 and 6: the deploy 503 window target
becomes about 25 s at today's size (lease handoff, 19 s restore, host start),
and the "restore before the lease, then catch up" variant described in 3.2
is the path to single digits if that matters later. The restore is bound by
LZ4 decompression and page writes on a 0.5 vCPU, not by the network: the
same file took about 4 minutes from this machine, where bandwidth dominates.

Uploading a snapshot in region is fast enough that the per-deploy snapshot in
11.3 is cheap: the job padded, snapshotted and uploaded the 1.02 GB copy
(935 MB on the wire) in under 100 s wall clock including its own 19 s restore.

### 11.3 `litestream replicate` on a freshly restored file (section 7, item 3)

Result: **it starts a new snapshot; the chain continues from it.** A restored
file has no local Litestream state, so the daemon's position is TXID 0 while
the replica's is N. 0.5.9 handles this explicitly (`db.go`,
`checkDatabaseBehindReplica`, for its issue #781): it logs
`detected database behind replica`, downloads the replica's latest L0 file
(228 MB here) as a local baseline, and the first sync then takes a full
snapshot of the current database as TXID N+1 and uploads it. From then on
WAL segments follow as usual. The previous snapshot stays until retention
removes it, so a restore in the next deploy picks the newest TXID either way.

Observed locally against the scratch container (restored file plus three
inserted rows): `detected database behind replica db_txid=0 replica_txid=1`,
`fetched latest L0 file from replica`, then a new local
`ltx/0/0000000000000002-0000000000000002.ltx` of 228,571,073 bytes.
In region (job `VERIFY4`: restore, insert one row, `litestream replicate`
for four minutes against the same replica path), the container listing
afterwards shows the whole sequence: the new snapshot
`storage.db/ltx/0/0000000000000002-0000000000000002.ltx` (228,571,066 bytes)
landed 85 s after the job started, 19 s of which were the restore, so the
re-snapshot and its upload took about a minute on 0.5 vCPU; within another
minute the snapshot level held `ltx/9/0000000000000001-0000000000000002.ltx`
and L1 held `ltx/1/0000000000000002-0000000000000002.ltx`, and the original
L0 file for TXID 1 had been compacted away. A restore now reads the L9 file.

Three consequences for the design:

1. Every deploy uploads one full snapshot (about 230 MB today, 935 MB at
   1 GB), in the background after the app is serving. Section 7 accepted this.
2. Until that upload finishes, the WAL segments of the new writer queue behind
   it: the recovery point for the first minute or so after a deploy is "the
   old writer's final sync", not 30 s. Added to section 5.
3. Litestream must open the database only after it is in place. The startup
   gate seeds `/tmp/storage.db` by renaming a staged copy over it, and a
   Litestream started earlier would keep the old inode. So the app starts
   `litestream replicate` itself after the seed (`LitestreamSupervisor`),
   and the entrypoint keeps `exec dotnet Server.dll`. Sections 3.1 to 3.3 are
   changed accordingly; PID 1 is the app, and SIGTERM reaches it directly.

### 11.4 `litestream sync` and the daemon's final sync (section 7, item 4)

Result: **both hold.**

- `litestream sync -wait -timeout N -socket PATH DB` talks to the daemon's
  control socket (`POST /sync`, `cmd/litestream/sync.go`) and blocks until the
  replica upload is done. The socket is **off by default**
  (`DefaultSocketConfig`: `enabled: false`, `/var/run/litestream.sock`), so
  the config enables it at `/tmp/litestream.sock`. In the Docker handover
  test the app's final sync returned in 44 ms with a few rows pending.
- On child exit (and on SIGTERM, which it forwards to an `-exec` child and
  waits for), the daemon closes each database: `db.Close` runs a last
  `db.Sync` and then `syncReplicaWithRetry`, retrying for up to
  `shutdown-sync-timeout` (default 30 s, `DefaultShutdownSyncTimeout`). So a
  writer that merely exits still gets a final upload; the explicit `sync`
  in 3.3 is what makes it happen before the lease is released.

### 11.5 Configuration keys (section 7, item 5)

Result: **section 3.5 used two keys that do not exist in 0.5.9, and 0.5.9
ignores unknown keys silently** (a config with `totally-bogus-key`,
`bogus-db-key` and `bogus-replica-key` replicated without a warning). The
previous `infrastructure/litestream.yml`'s `snapshot-interval: 1h` on the
replica had therefore been a no-op since the 0.5 upgrade, and the spec's
`retention: 72h` would have been one too. The real keys
(`cmd/litestream/main.go`, `store.go`, `compaction_level.go`):

| Key | Level | Default | Meaning |
|---|---|---|---|
| `snapshot.interval` | global | 24h | full snapshot cadence (L9) |
| `snapshot.retention` | global | 24h | how long snapshots and their LTX files are kept; the point-in-time window |
| `levels[]` with `interval` | global | 30s, 5m, 1h | L1 to L3 compaction cadence; up to eight levels |
| `l0-retention` / `l0-retention-check-interval` | global | 5m / 15s | how long raw L0 segments stay |
| `retention.enabled` | global | true | master switch for retention |
| `shutdown-sync-timeout` / `shutdown-sync-interval` | global | 30s / 500ms | the final sync in 11.4 |
| `socket.enabled` / `socket.path` | global | false / `/var/run/litestream.sock` | control socket for `litestream sync` |
| `replica.sync-interval` | replica | 1s | upload cadence; the RPO |
| `replica.endpoint`, `account-name`, `account-key`, `sas-token`, `bucket`, `path` | replica (abs) | | as in 11.1 |
| `replicas:` (list) | db | | deprecated but still accepted; `replica:` is the 0.5 form |

Section 3.5 and `infrastructure/litestream.yml` now use these.

### 11.6 Other observations

- `litestream replicate -once` of the 322 MB file into Azurite on this
  machine completed its sync and then did not exit for over 25 minutes
  (killed). Against the real account the same command exited normally. Noted
  as an Azurite-on-Windows artefact; the daemon's `-once` mode is not used
  anywhere in the design.
- In the Docker handover test (9.2), writer B acquired the lease 8 s after
  A received SIGTERM: A's final sync (44 ms), the share copy, lock release,
  then lease release; B was polling every 2 s.
- Rollout step 2 as built: the lease is acquired concurrently with the
  startup gate (it has to be, because the old writer releases its lease only
  after being told to stop, and it is told to stop only once this revision's
  probe passes). If the lock is free but the lease is still held, the host
  waits for the lease after the gate has stopped, which is a short gap on the
  port that the liveness probe (30 s period, three failures) rides out. With
  a 30 s lease that gap is at most about 30 s and only follows a crashed
  writer.

### 11.7 Found in rollout step 1: 0.5.9 snapshots the whole database on every WAL restart

Observed in production within an hour of step 1 going live (2026-10-24,
20:54 to 21:42 UTC, revisions 89 to 92): the replica received a 228 MB LTX
file, the size of a full snapshot, every one to three minutes in steady
state, not only after a deploy. Seven landed between 21:18 and 21:31. Blob
transactions ran at about 14,000 an hour against the 3,000 to 6,000 a day
in section 6; the old writer's final `litestream sync` timed out at 20 s
behind one of the uploads during the 21:25 deploy, and its lease expired
mid-shutdown; the container's working set reached 865 MB of 1 GiB. Step 1
was rolled back the same evening (`enableBlobReplication=false`, PR #176).

Reproduced on this machine with the production copy, the scheduler on and
Litestream logging at debug: every full-size file carried a reason.

```
msg=sync txid=000000000000000b offset=32 snap=true reason="wal truncated, snapshotting for complete page coverage"
msg=sync txid=000000000000000e offset=32 snap=true reason="wal restarted, snapshotting for complete page coverage"
```

The first is the restart-on-restored-file case of 11.3 and is expected once.
The second is the cause: SQLite restarts its write-ahead log from the
beginning after a complete checkpoint, and 0.5.9's `verify` answers a
restart it did not perform itself with a full snapshot. Litestream
checkpoints every minute (`checkpoint-interval`) and releases its read lock
to do so; this app writes often enough that the next app transaction
restarts the WAL inside that window, so the snapshot fired every minute or
two. The May 2026 scaling-mode experiment ran the same version against the
same pattern, which is the likely origin of the 25 GiB of leftovers that
production's retention sweep deleted at 20:56.

Litestream 0.5.15 ("fix(db): harden checkpoint snapshots", PR 1292) is the
upstream fix: "unnecessarily large LTX output when WAL state changed around
a checkpoint", with checkpoint boundaries falling back to a normal catch-up
sync. Verified here with 0.5.17 (the current release) in the same
reproduction: nine minutes, one full snapshot at start, every other upload
1.4 to 1.7 MB (a feed-refresh batch), no `reason=` line at all. The image
moves to 0.5.17 (`src/Server/Dockerfile`, checksum
`cfb371176d164437ae869f8351cfde49bd1804ae71c61923f75c9cba9c9c006d`), the
Docker durability test passes on it, and step 1 is switched back on with it.

Two lessons for the rest of the rollout:

- The Docker test's database is 160 KB, so a "full snapshot every sync"
  looked like nothing there. The shadow period on the real size is what
  caught it, and the blob Transactions metric is the signal to watch after
  every change to Litestream's version or config.
- The sanity check for the seed held throughout: a restore of the live
  replica during the episode matched `/api/healthz` exactly (151,884 posts,
  225 subscriptions, 5,509 user-post rows, max id 151884, quick_check ok).
  Oversized, not wrong.
