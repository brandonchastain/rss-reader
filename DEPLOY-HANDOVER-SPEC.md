# Deploy handover: the old revision serves until the new one is ready

Status: proposed, 2026-10-24, for review. Not implemented.
Scope: change the order of the deploy handover so the old revision does its
final copy and gives up the writer role *while it is still serving*, and the
new revision only passes its startup probe once the real host is up. The
users' 503 window goes from about 61 s to about 0; in its place is a
read-only window of about 20 s in which reads are served and writes are
refused with a retryable status. Nothing about the schema, the API surface,
the share layout or the blob replica changes. The design works with the
share-based seed that runs today and with the blob restore of
BLOB-DURABILITY-SPEC.md rollout step 3 (PR #179), and it survives the
removal of the share in that spec's step 5 with one substitution (section 3.8).

ARCHITECTURE.md "Deploy handover" describes the current sequence; this spec
replaces it when the rollout in section 8 completes.

## 1. Why

Measured on the PR #178 deploy (2026-10-24 22:28 UTC, 1 s probe of
`/api/user`, raw container timestamps; the same shape holds across 55 deploys
in 14 days of Log Analytics):

| Step | Time | Who |
|---|---|---|
| New revision's container starts, startup gate answers `/api/healthz` 200 | +0 s | app |
| Container Apps moves traffic to the new revision; every API route now returns 503 from the gate | +5 s | platform |
| Container Apps sends SIGTERM to the old revision | +36 s (31 s after the switch, constant, not configurable) | platform |
| Old revision drains, runs the final 320 MB copy to the share, releases lock and lease | +43 s (7 s) | app |
| New revision sees the lock gone | +61 s (18 s: the CIFS attribute cache, `actimeo=30`) | platform |
| New revision seeds `/tmp` from the share | +68 s (7 s) | app |
| Real host answers | +68 s (<1 s) | app |

Users see 503 for the 61 s between the traffic switch and the real host.
Only 14 s of that is work (copy plus seed). The rest is two waits the app
cannot shorten from where it stands: the platform's 31 s between "probe
passed" and SIGTERM, and the share's 17 s before a deleted file is seen.

Both waits exist because of the order. The gate answers the probe *before*
it has a database, because the old revision only releases the lock on
SIGTERM, and SIGTERM only comes after the probe passes. So the probe must
pass early, traffic moves early, and the new revision spends the whole
handover answering 503 to live traffic. BLOB-DURABILITY-SPEC.md 3.2 keeps
this order and gets the window to about 25 s by making the restore faster;
this spec removes the order constraint instead, which takes the window to
about 0 at any database size and composes with that work.

## 2. Goals and non-goals

Goals

1. No request to the API returns 503 because of a deploy. Target: a longest
   non-200 run of 0 s on the deploy-time probe; 3 s is the failure threshold.
2. The old revision never serves after a write that the new one does not
   have. Exactly one revision accepts writes at any instant, and the final
   copy or sync happens after the last accepted write.
3. Reads keep working throughout. Writes are refused for a bounded window
   (target about 20 s with the share seed, about 30 s with today's blob
   restore) with a status the frontend turns into a retry, not an error.
4. Every failure falls back to today's sequence or to a crash-equivalent the
   existing recovery already handles. The new protocol never makes a deploy
   less safe than today's; at worst it makes it as slow.
5. The first deploy that carries this code, whose *old* revision does not
   understand it, behaves exactly as today.

Non-goals

- Shortening the platform's 31 s SIGTERM delay or the SMB attribute cache.
  Both stay; the design arranges that neither is on the user's path. (The
  attribute-cache fix proposed in the 2026-10-24 deep dive, probing the lock
  with an open instead of `File.Exists`, is a separate, smaller PR that this
  design benefits from but does not require: section 3.4.)
- Zero write downtime. That needs two writers or write forwarding; the
  single-writer shape stays.
- Any change to the blob replica, the lease, or Litestream's configuration.

## 3. Design

### 3.1 The idea in one paragraph

The new revision, instead of waiting passively for the lock, asks for it: it
writes a *handover request* marker next to the lock file. The old revision's
lock loop, which already runs for the heartbeat, notices the marker within a
couple of seconds and runs its shutdown sequence early, while it is still the
revision that traffic goes to: it switches to read-only, stops its writers,
syncs Litestream if enabled, runs the final copy, writes a *released* record
into the marker, and gives up the lock and the lease. It keeps serving reads
from its now-frozen database. The new revision, which has been answering the
startup probe with 503 all this time so that traffic has not moved, seeds (or
restores), builds the real host, and only then answers 200. Container Apps
moves traffic to a revision that is ready, and 31 s later sends SIGTERM to the
old one, which has nothing left to do.

### 3.2 Files

All on the share, in the lock's directory (`/data/db/`):

| File | Written by | Meaning |
|---|---|---|
| `storage.db.lock` | the writer, as today | Held by the current writer. New: the first line gains a capability token, `handover=v1`, so a requester knows before asking whether the holder understands requests |
| `storage.db.handover` | the new revision (requester); the old revision appends to it | The request. Exists only during a handover; the requester deletes it when it has the lock, the holder deletes a stale one |
| `storage.db` | the old revision's final copy, as today | unchanged |

The marker's contents, one record per line, newest last:

```
requested  2026-10-24T22:29:01Z  host=rss-reader-api--0000095-abc pid=1 revision=rss-reader-api--0000095 proto=v1
accepted   2026-10-24T22:29:03Z  host=rss-reader-api--0000094-xyz pid=1
synced     2026-10-24T22:29:05Z  host=rss-reader-api--0000094-xyz ltx_txid=4411 lease=released
released   2026-10-24T22:29:12Z  host=rss-reader-api--0000094-xyz bytes=323022848 copy_utc=2026-10-24T22:29:12Z
```

or, instead of `accepted`/`released`:

```
refused    2026-10-24T22:29:03Z  host=... reason=final-copy-failed
```

The requester creates the marker with `FileMode.CreateNew` (atomic: of two
requesters, exactly one creates it) and refreshes its mtime every 5 s while it
is waiting, exactly as the lock holder heartbeats the lock. Appends by the
holder are small and go through `FileShare.ReadWrite`; the only reader is the
requester, which re-reads the whole file on each poll and acts on the last
line. A partial line at the end (torn append) is ignored until complete.

### 3.3 The sequence

```
New revision N (booting)                      Old revision O (serving, holds lock + lease)
----------------------------------------      -------------------------------------------
1. Gate starts. /api/healthz -> 503
   {phase:"waiting"}; everything else
   -> 503 Retry-After: 5. Traffic is
   still on O, so nobody sees these.
2. Read the lock file. If it is absent
   or stale: take it, seed, go to 9.
   If it lacks `handover=v1`: legacy
   mode (3.6). Otherwise:
3. Create storage.db.handover with a
   `requested` line. Heartbeat it
   every 5 s. Poll it, the lock and
   the lease every 1 s.
                                              4. Lock loop (every 2 s): open the marker.
                                                 Fresh `requested` line, not our own host:
                                                 append `accepted`. Enter the handover:
                                              5. Flip WriterState to ReadOnly. Pause the
                                                 FeedScheduler, BackgroundWorker and
                                                 PostRetentionService; wait up to 5 s for
                                                 in-flight writes. HTTP writes now return
                                                 503 (3.5). Reads continue.
                                              6. If Litestream is enabled: wal_checkpoint,
                                                 `litestream sync -wait`, stop the child
                                                 (BLOB-DURABILITY-SPEC 3.3 steps 3 to 4).
                                                 Append `synced` (ltx_txid) to the marker and
                                                 release the lease (metadata: ltx_txid). This
                                                 is PR #179's shutdown order: the lease goes
                                                 right after the sync so a blob-restoring N
                                                 can start while the share copy below runs.
                                              7. Final share copy (RunBackupCycleAsync
                                                 force:true), as today's StopAsync does.
                                                 Record size and mtime of the published copy.
                                              8. Append `released` to the marker. Release
                                                 the lock file. O is now a lame duck:
                                                 read-only, serving, owning nothing.
9. Blob restore on (PR #179): poll sees
   the lease free after `synced`; take
   it, `litestream restore`, quick_check,
   rename. Share seed (today): poll sees
   `released`; take the lock, check the
   share copy's size against the line,
   seed /tmp. Either way the lock is
   taken before the real host (the
   backup service does it under #179).
   Delete the marker.
10. Stop the gate, build the real host.
    /api/healthz -> 200. (~1 s of
    refused connections on the port;
    not routed, so not seen.)
11. Startup probe passes (2 s period).
    Container Apps moves traffic to N.
                                              12. ~31 s later: SIGTERM. StopAsync sees the
                                                  handover is complete: no copy, no release,
                                                  exit within a few seconds.
```

Nothing in steps 1 to 11 requires the platform to do anything on the user's
path. The two platform waits in section 1 still happen (step 11's probe
cadence is 2 s; step 12 is 31 s) but traffic is on a ready revision during
both.

### 3.4 Visibility and the attribute cache

Three signals cross the share during a handover and each needs to be seen
within a second or two, not at the 30 s cadence of the CIFS attribute cache:

| Signal | Reader | How it is read |
|---|---|---|
| marker appears | O | an open attempt, every 2 s |
| `accepted`/`released`/`refused` lines | N | open and read the file, every 1 s |
| lock file deleted | N | the lease when enabled (blob, no cache), else an open attempt |

Reading a file's *contents* through an open handle is coherent on SMB: the
server breaks the other client's oplock on write, so N sees O's appends on
its next read without waiting for metadata to expire. What is cached for up
to `actimeo` is the directory entry: `File.Exists` on a name that was absent
a moment ago can keep answering "absent" from the negative dentry. That is
the 17 s in section 1, and it is why the design avoids `File.Exists` and
mtime reads on the hot path everywhere and uses open attempts instead.
Whether an open attempt on a negatively cached name goes to the server on
Linux CIFS is verification item 1 (section 7). If it does not, the fallback
is the mount option `actimeo=1` on the volume in `main.bicep`, which the deep
dive also proposed; at one extra metadata round trip per poll it costs
nothing measurable. Either way the lease, when enabled, is N's primary
signal for "O is done": it is a blob call and has no cache.

### 3.5 The read-only window

`RssAppConfig.IsReadOnly` is a boot-time constant today, consulted by
`SqliteDbConnections`, `RepositoryFactory`, the repositories (schema
creation) and `ReadOnlyActionFilter`. The handover needs the same behaviour
switched on at run time. Changes:

- A `WriterState` singleton with `Mode` of `ReadWrite`, `ReadOnly` (handing
  over) or `Orphaned` (3.7), and `Reason`/`Since`. `SqliteDbConnections`
  consults it in `OpenWrite` and throws `ReadOnlyException` (exists today for
  the replica path). `ReadOnlyActionFilter` is always registered and
  consults it per request. The boot-time `IsReadOnly` becomes
  `WriterState.Mode = ReadOnly` at startup, so the reader path, while it
  exists, is a special case of the same state rather than a parallel one.
- The background writers (`FeedScheduler`, `BackgroundWorker`,
  `PostRetentionService`) check `WriterState` before each unit of work and
  skip while not `ReadWrite`. The handover waits up to 5 s for the unit in
  flight; a feed-refresh batch commits in well under a second.
- HTTP writes (POST, PUT, PATCH, DELETE) during the window return
  **503 Service Unavailable** with `Retry-After: 10`, a header
  `X-RssReader-Writer: read-only; reason=deploy`, and the JSON body
  `{ "error": "read-only", "reason": "deploy", "retryAfterSeconds": 10 }`.
  Not 405 as the replica filter returns today: 405 says "never", 503 with
  `Retry-After` says "in a moment", and the frontend's existing write-behind
  outbox already treats a non-2xx on mark-read as "replay later"
  (`FeedClient.TrySendAsync`, `app.js markReadBeforeNavigate`). The replica
  filter's 405 is left as is until the reader role is removed.
- `/api/healthz` on O keeps returning 200 throughout (liveness must not
  restart a lame duck mid-copy) and adds `"writer": { "mode": "read-only",
  "reason": "deploy", "sinceUtc": ... }`. On N's gate it returns 503 with
  `{ "status": "starting", "phase": "waiting" | "seeding" }`.

Frontend (`WasmApp`), a small change: on any 503 carrying the
`X-RssReader-Writer` header, show a banner in the shell style of
`Home.razor` ("deploying: read-only for a few seconds") for `Retry-After`
seconds plus one probe interval, and queue the write through the outbox
where the operation has one (mark read, save). Operations with no outbox
entry (subscribe, tag edits, import) keep their current error surface with
the message replaced by "the reader is deploying, try again in a few
seconds" and a retry control; nothing is lost because nothing was accepted.
Reads are unaffected and the banner never blocks them.

What a user sees, in order: the page keeps working; a mark-read during the
window is applied locally at once and lands on the server a few seconds
later; an add-feed during the window asks them to try again shortly. No
spinner-until-timeout, no "network error", no 61 s of 503.

Stale reads: from step 8 until the traffic switch at step 11, O serves a
database that nothing is writing to, so it is current. After the switch, O
only finishes in-flight requests. The window in which O could serve a read
older than a write N accepted is therefore bounded by one request's duration.

### 3.6 Legacy mode and refusal

N falls back to today's sequence, gate answering 200, waiting for the lock to
be released on SIGTERM, when any of these hold:

- the lock file has no `handover=v1` token (the holder predates this spec;
  this is the first deploy after the feature ships);
- no `accepted` line appears within `AcceptTimeout` (15 s) of the request;
- the holder appends `refused`;
- the marker cannot be created for a reason other than "already exists"
  (unwritable directory, which `ShareWriterLock` already handles the same way).

O refuses, with a reason, when the final copy fails quick_check or the size
check, when the Litestream final sync fails outright (a timeout is logged and
tolerated as in BLOB-DURABILITY-SPEC 5), or when it is already in a handover
with a different requester whose marker is fresh. On refusal O rolls back:
`WriterState` returns to `ReadWrite`, the background writers resume,
Litestream restarts if it was stopped. The read-only window in that case is
the time to the failure (a failed copy is noticed within a few seconds; a
quick_check failure at the end of a 7 s copy is the worst case).

Legacy mode is the safety net for every unknown: it is the sequence that has
run on every deploy for weeks and it costs exactly what it costs today.

### 3.7 Staleness and crashes

The marker is fresh while its mtime is younger than `MarkerStaleAfter`
(20 s, four missed 5 s heartbeats). The lock's own heartbeat and stale rule
are unchanged (30 s / 3 min) except that the holder's loop now ticks every
2 s to check for a marker; the heartbeat touch still happens every 30 s.

| Event | Who notices | What happens | Window cost |
|---|---|---|---|
| N dies before O accepts | O: marker goes stale | O deletes the marker; nothing else changed. Container Apps restarts N's container (N'), which starts at step 2 | none |
| N dies after O accepted, before O released | O: marker stale during steps 5 to 7 | O aborts: back to `ReadWrite`, writers resume, Litestream restarts, marker deleted. N' starts a new request | read-only up to `MarkerStaleAfter` (20 s) plus the abort |
| N dies after O released | O: marker stale, lock still free, no new request for `ReclaimAfter` (60 s) | **Reclaim.** O compares the share copy's size and mtime with what it recorded at step 7. Equal: nothing has written since its copy, so its `/tmp` is the latest state; O retakes lock and lease and returns to `ReadWrite`. Different: another writer published a copy and died; O's `/tmp` is behind it, so O moves to `Orphaned`: read-only, `/api/healthz` says so, error logged, and the next deploy or a container restart is the fix. N' meanwhile starts at step 2 and, if it comes up, takes the free lock normally | read-only until N' is up or 60 s |
| O dies mid-copy (after accept, before release) | N: lock heartbeat stops, lease stops renewing | Same as a crash today. N waits for the lease to expire (30 s) when enabled, else for the lock to go stale (3 min), then seeds from the previous share copy (up to `BackupInterval` old) or, with PR #179, restores from blob (up to one sync interval old). The marker is left behind; N deletes a marker whose `requested` line is its own, and anyone deletes one that is stale | 503 for the stale wait, as today; data loss as a crash today |
| O dies after release | nobody needs to | N proceeds; O's SIGTERM never matters | none |
| O's SIGTERM arrives mid-handover (Container Apps restarted it for an unrelated reason, or an operator stopped the revision) | O | `StopAsync` finishes the sequence from wherever it is: the copy if not done, the release if not done. It is the same code path, entered from a different trigger | none |
| Two requesters (N restarted by the platform while N is still alive) | marker `CreateNew` | The second sees an existing fresh marker not its own and waits; it takes over the marker only when it is stale. One of them gets the lock; the other finds it held and fresh and keeps waiting as today | none |
| Share unreachable during the handover | O: copy throws; N: polls throw | O refuses (3.6) if it can still write the marker, else just aborts; N falls back to legacy mode after `AcceptTimeout` | today's cost |

The two rules that make this safe are: O never releases before the copy is
published and verified, and O never returns to `ReadWrite` after releasing
unless the share proves nothing wrote in between. Everything else is a
timeout that degrades to today's behaviour.

### 3.8 Interaction with the blob lease, Litestream and the blob restore

- **Lease.** Released at step 6 by the handover, right after the Litestream
  sync and before the share copy, which is the shutdown order PR #179
  establishes (sync, lease release, share copy, lock release).
  `WriterLeaseHostedService.StopAsync` becomes "release if still held". The
  `synced` line's `ltx_txid` and the lease metadata carry the same value
  (BLOB-DURABILITY-SPEC 3.3 step 5). N uses lease acquisition as its
  primary "the replica is final" signal because it has no cache. Releasing
  the lease before the lock is safe because the lease protects the replica
  and the lock protects the share copy: a blob-restoring N opens neither
  the share copy nor the lock until the backup service takes the lock
  before its first cycle, by which time O has released it (or N waits).
- **Litestream enabled on O.** Step 6 runs the same final sync as today's
  `LitestreamSupervisor.StopAsync` and then stops the child, before the
  lease release, so N's `litestream replicate` never overlaps O's on the
  replica path. A lame-duck O does not restart Litestream; an aborted
  handover does, and an abort after step 6 must re-acquire the lease first
  (if N already holds it, the abort is not possible and O stays read-only
  as in the "N dies after release" row of 3.7, keyed on the lease rather
  than the lock).
- **Restore from blob on N (PR #179, flag off today).** Step 9 becomes
  "acquire the lease, `litestream restore`" and runs concurrently with O's
  share copy, so the copy drops out of the critical path. The restore is
  19 s at today's size (BLOB-DURABILITY-SPEC 11.2), so the read-only window
  is about 25 s until that spec's v2 startup (restore before the lease,
  catch up after) lands; the 503 window stays 0, which is the point. Any
  restore failure falls back to the share path exactly as PR #179 does
  (wait for the lock, seed), which here means waiting for `released`.
  The share copy at step 7 still runs while the share is authoritative
  (that spec's steps 3 and 4) and goes when the share goes. PR #179's
  `deploy.yml` probe (1 s poll of `/api/item/timeline`, longest 5xx run in
  the job summary) is the metric this design drives to 0; section 9.3 adds
  the write probe beside it.
- **After the share is removed (that spec's step 5).** The marker needs a
  home. Substitute a blob `litestream/handover-request` written with
  `If-None-Match: *` (the same atomic create) and appended to with
  blob metadata or a block append; the protocol is identical and loses the
  attribute-cache question entirely. The `WriterState` machinery, the
  frontend behaviour and the staleness rules carry over unchanged. This is
  noted here so neither spec paints the other into a corner; it is not part
  of this rollout.

### 3.9 Self-hosting

With `BackupDbPath` empty there is no gate, no lock and no handover, as
today. A self-hosted container with a share-like directory and a single
container (no revision overlap) never has a requester, so the holder's 2 s
marker check is the only new cost: one open attempt on a local or SMB path.

### 3.10 Configuration

Under `RssAppConfig:Handover`, all with defaults; Bicep exposes only the
first:

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` until rollout step 2 | Requester asks; holder accepts. Off: holder still advertises `handover=v1` and logs requests (step 1), requester runs legacy mode |
| `HolderPollInterval` | 2 s | How often the lock loop opens the marker |
| `RequesterPollInterval` | 1 s | How often N reads the marker and tries the lease |
| `RequesterHeartbeat` | 5 s | Marker mtime refresh |
| `MarkerStaleAfter` | 20 s | Marker older than this is abandoned |
| `AcceptTimeout` | 15 s | No `accepted` within this: legacy mode |
| `DrainTimeout` | 5 s | Wait for in-flight background writes at step 5 |
| `ReclaimAfter` | 60 s | Free lock, no request, released: reclaim or orphan (3.7) |
| `WriteRetryAfterSeconds` | 10 | The `Retry-After` on refused writes |

`HostOptions.ShutdownTimeout` (110 s) and `terminationGracePeriodSeconds`
(120) stay, because legacy mode and a SIGTERM mid-handover still need them.
They shrink with BLOB-DURABILITY-SPEC step 5, not here.

## 4. What changes in the code

- `ShareWriterLock`: writes `handover=v1` into the lock file; exposes
  `HasCapability(string)` for a foreign lock; the heartbeat loop ticks at
  `HolderPollInterval` and raises an event when a fresh foreign marker
  appears. Open-based probes replace `File.Exists` on the hot path.
- New `HandoverMarker`: create, heartbeat, append, read-last-line, stale
  check, delete-if-mine. Pure file operations; tested on a temp directory.
- New `HandoverCoordinator` (holder side): the state machine of 3.3 steps 4
  to 8 and 3.7, driven by `DatabaseBackupToFileService` (which owns the
  copy), `LitestreamSupervisor` (sync and stop), `WriterLeaseHostedService`
  (release) and `WriterState`. `DatabaseBackupToFileService.StopAsync`
  becomes "finish the coordinator's sequence if incomplete".
- `WriterStartupGate.RunAsync`: the requester side of 3.3 steps 1 to 3 and 9,
  with legacy mode as the fallback branch; `/api/healthz` on the gate returns
  503 only when a handover was accepted.
- `WriterState` singleton; `SqliteDbConnections`, `ReadOnlyActionFilter`,
  `FeedScheduler`, `BackgroundWorker`, `PostRetentionService` consult it.
  `ReadOnlyActionFilter` is always registered.
- `/api/healthz`: the `writer` block.
- `WasmApp`: the 503 banner and the message substitution in 3.5.
- `appsettings.json`: `Logging` override to Information for
  `RssReader.Server.Services` so the holder side is visible in production
  (deep dive finding 3; today the shutdown lines are never emitted).
- `deploy.yml`: thresholds on the handover probe (section 9.3).

Lines of code are modest; the state machine is the part that needs the tests
in section 9 more than it needs cleverness.

## 5. Failure modes, before and after

| Event | Today | After |
|---|---|---|
| Deploy, normal | 61 s of 503 on every route | 0 s of 503; about 20 s in which writes return 503 with `Retry-After` and reads work |
| Deploy, old image predates the feature | n/a | identical to today (legacy mode) |
| Deploy, final copy fails on the old revision | copy error logged, lock released anyway, new revision seeds a copy up to 15 min old | old revision refuses and goes back to read-write; new revision runs legacy mode; on SIGTERM the copy is retried as today |
| New revision crashes during the handover | 503 continues until the platform restarts it; old revision has already been SIGTERMed, so writes since its last copy are in its final copy and nothing is lost | old revision aborts or reclaims (3.7); users were never on the new revision; if reclaim is impossible, read-only until the next deploy, visible on `/api/healthz` |
| Old revision crashes during its final copy | lose up to 15 min (3 min stale wait, then seed from the previous copy) | same |
| Platform SIGTERMs the old revision early | final copy on SIGTERM | the same sequence, from wherever it was |
| Liveness probe during the window | n/a | old revision answers 200 throughout; new revision's liveness probe does not start until its startup probe passes |

## 6. Expected timings

At today's size (323 MB) and the share seed, from the new container's start:

| | Share seed (today's path) | Blob restore (PR #179 on) |
|---|---|---|
| Gate up, lock read, marker written | +2 s | +2 s |
| Holder notices, drains, read-only begins | +5 s | +5 s |
| Litestream final sync, lease released | <1 s | <1 s (+6 s) |
| Final share copy | 7 s | 7 s, concurrent with N's restore |
| Release seen by the requester | +14 s (`released`, lock) | +7 s (lease poll) |
| Seed or restore | 7 s | 19 s |
| Real host up, probe passes | +23 s | +28 s |
| **Read-only window** | **about 18 to 20 s** | **about 23 to 25 s** |
| **503 window** | **about 0 s** | **about 0 s** |
| Old revision receives SIGTERM | +54 s, exits in about 1 s | +59 s, same |

The read-only window is bounded below by copy plus seed on the share path
and by the restore alone on the blob path, both of which the blob spec's
later steps shrink independently (the copy goes with the share, the restore
drops to single digits with its v2 startup). The 503 window does not depend
on size at all.

## 7. To verify before building

1. **Negative dentry caching on Linux CIFS.** On a container with the share
   mounted as Container Apps mounts it: `File.Exists` and then an open
   attempt on a name another client creates a second later. Does the open
   see it immediately? If not, measure with `actimeo=1` as a mount option.
   One hour; decides 3.4.
2. **Append visibility.** Two containers, one appending lines to a file on
   the share every second, the other opening and reading it every second.
   Confirm each line is visible within one poll, and that a torn read is at
   most a partial last line. Decides whether 3.2's append is enough or the
   holder should write a separate `.accepted` file.
3. **Container Apps and a 503 startup probe.** Confirm that a revision
   answering 503 for up to 40 s keeps the old revision active and keeps
   traffic there (expected from the 2 s × 150 budget already configured),
   and confirm what the revision's state shows in the portal and the
   deploy's `az containerapp` polling meanwhile.
4. **Drain time.** How long the FeedScheduler and BackgroundWorker take to
   finish an in-flight batch on production data, to size `DrainTimeout`.
   Read from existing logs, no experiment.

## 8. Rollout

Each step ships on its own, is observable in the next two deploys (the
first deploy after a step has an old revision without it), and rolls back by
reverting the flag or the PR.

1. **Advertise and observe.** `handover=v1` in the lock file; the holder's
   2 s loop detects and logs a marker but takes no action; the requester
   writes a marker and then runs legacy mode regardless. Also the logging
   override from section 4. Measure across several deploys: time from marker
   creation to the holder's log line (target under 3 s; the 17 s figure
   would mean verification 1 failed and the mount option is needed).
2. **Protocol on, behind the flag.** `Handover:Enabled=true` via a Bicep
   parameter. The deploy-time probe (9.3) reports the 503 window and the
   read-only window separately. Expect 0 and about 20 s. Any refusal or
   fallback shows as a legacy-mode line in the requester's log and as
   today's 61 s on the probe.
3. **Frontend.** The banner and message changes in 3.5. Independent of the
   backend steps; it can ship first, since a 503 with the header never
   happens until step 2.
4. **Thresholds.** `deploy.yml` fails the job when the 503 run exceeds 3 s
   or the read-only run exceeds 45 s. Turn on after five clean deploys at
   step 2.

Rollback at any step is the flag or the previous image; the lock file's
extra token is ignored by the previous image, which reads only mtime.

## 9. Tests

The property to protect: across any interleaving of a deploy with crashes
of either side, no request returns 503 unless both revisions are unable to
serve it, no write is acknowledged by a revision that does not hold the
lock, and the revision that ends up writing has every write the other one
acknowledged.

### 9.1 Unit tests (dotnet test, in `ci.yml` and `deploy.yml`)

Against a temp directory with a fake clock:

- **Marker.** Create is exclusive; heartbeat moves mtime; last-line parsing
  handles each record kind and a torn tail; stale detection; delete-if-mine.
- **Holder state machine.** Fresh marker triggers accept; stale marker
  before accept is deleted; stale marker after accept aborts and restores
  `ReadWrite`; copy failure refuses and restores; success releases lock then
  lease and leaves `WriterState.ReadOnly`; SIGTERM mid-sequence finishes it
  exactly once; reclaim when the share copy is unchanged, orphan when it
  changed; a lock without the capability token is never accepted against.
- **Requester state machine.** Lock without token → legacy; no accept in
  `AcceptTimeout` → legacy; `refused` → legacy; `released` → takes lock,
  checks size, seeds, deletes marker; gate answers 503 on `/api/healthz`
  only between accept and host start.
- **WriterState.** `OpenWrite` throws in `ReadOnly`; the filter returns 503
  with the headers in 3.5 for each write method and lets GET through; the
  background services skip a tick while not `ReadWrite`.

### 9.2 Handover integration test (Docker, in `ci.yml` beside the durability test)

Two containers of the real image sharing a `/data` volume, a client writing
a row every 200 ms and reading the timeline every 200 ms throughout:

1. **Normal handover.** Start A; start B; assert the client never saw a
   5xx on a read, saw 503 with `Retry-After` on writes only inside a window
   under 40 s, and that B serves every row A acknowledged. Then SIGTERM A
   and assert it exits within 5 s without copying.
2. **Requester dies.** Start B, kill it after A's `accepted` log line;
   assert A is back to `ReadWrite` within `MarkerStaleAfter` plus 5 s and
   accepts writes; start B again; assert the normal case.
3. **Requester dies after release.** Kill B after A's `released` line;
   assert A reclaims within `ReclaimAfter` plus 5 s and serves writes.
4. **Holder dies mid-copy.** SIGKILL A after `accepted`; assert B falls
   through to the stale path and comes up with every row acknowledged
   before A's last periodic copy (the test sets `BackupInterval` to 5 s).
5. **Legacy holder.** Start A with `Handover:Enabled=false` and no
   capability token (an environment switch in the test image); start B;
   assert B logs legacy mode, answers 200 on its gate, and the sequence
   completes as today once A is SIGTERMed.

Budget: under five minutes with the timeouts shortened by configuration.

### 9.3 Deploy-time verification (`deploy.yml`)

PR #179 adds a 1 s probe of `/api/item/timeline` that records the longest
non-200 run. Extend it to also issue a harmless write every second (a
mark-read of a fixed post, or a dedicated `POST /api/healthz/write-probe`
that touches a one-row table and is admin-only) and record the longest run
of 503s on it separately, plus the `X-RssReader-Writer` header's presence.
The job summary then shows "503 window: 0 s; read-only window: 19 s" per
deploy, and step 4 of the rollout puts thresholds on both. Query Log
Analytics after the rollout for the holder's `accepted` and `released`
lines and the requester's `seeded` line; the absence of any one fails the
job, since it means a fallback was taken silently.

### 9.4 Browser end-to-end (existing Playwright suite)

One added scenario: with the API in `ReadOnly` (a test-only endpoint flips
`WriterState`), mark a post read, confirm the banner appears and the row
flips at once; flip back; confirm the outbox replays and the server agrees.

## 10. Open decisions for the owner

- Whether refused writes should be 503 (this spec) or 423 Locked. 503 is
  what proxies and the existing outbox already understand; 423 is more
  precise and never confused with a real outage in dashboards. The header
  distinguishes them either way.
- Whether to build the marker on the share now or wait for the blob
  substitution in 3.8 and build it once. The share version is a few weeks
  of 61 s deploys sooner and the state machine, which is most of the work,
  is shared; the recommendation is to build on the share with the marker
  behind a small interface so the blob version is a second implementation.
- Whether rollout step 3 (frontend) should ship first, since it is harmless
  on its own and removes a dependency from step 2.
