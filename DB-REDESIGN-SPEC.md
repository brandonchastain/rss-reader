# Database redesign: store posts once, per-user state separately

Status: approved by the owner, 2026-10-23.
Scope: replacement of the SQLite schema with a one-shot, in-place data
migration that preserves posts, read/saved state, subscriptions and tags.
The HTTP API does not change.

## 1. Why

Today every post is stored once **per subscriber** (`Items` and `ItemContent`
are keyed `UNIQUE(FeedUrl, UserId, Href)`), while fetching is done once **per
URL** with shared `ETag`/`Last-Modified` validators. The two models disagree
the moment a second user subscribes to a URL that is already being fetched:
the add-time fetch gets a 304, nothing is written for the new user, and later
fetches only fan out *new* posts. This is the bug observed in production on
2026-10-21 with `https://blog.cloudflare.com/rss/`.

Secondary costs of the current shape: content is stored twice per subscriber
(`Items.Content` and `ItemContent.Content`), the FTS index is built per
subscriber, tags are stamped onto every item row, and "clear posts" deletes
data.

## 2. Principles

1. **A feed is a thing, not a per-user row.** Fetch schedule, validators and
   posts hang off the feed. Users attach to feeds through subscriptions.
2. **A post is stored once.** Per-user facts live in a sparse state table;
   the absence of a row means "unread, not saved".
3. **Per-user knobs live on the subscription**, never on posts: tags and the
   "hide posts before" watermark.
4. **Tags are entities owned by the user**, so the hidden flag has a home and
   renames are one update.
5. **Nothing destructive on read paths.** "Clear posts" and "delete feed" are
   subscription-level operations; shared posts are only removed by a
   retention job.

## 3. Schema

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE Users (
    Id          INTEGER PRIMARY KEY,
    Username    TEXT NOT NULL UNIQUE,
    CreatedUtc  TEXT NOT NULL
);

-- One row per login identity. Replaces Users.ProviderUserId / AadUserId and
-- the email-hash link column; a person may link several providers.
CREATE TABLE UserIdentities (
    Provider    TEXT NOT NULL,       -- 'microsoft' | 'google' | 'github' | 'email-link'
    Subject     TEXT NOT NULL,       -- oid / sub / login / email hash
    UserId      INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    PRIMARY KEY (Provider, Subject)
);
CREATE INDEX IX_UserIdentities_User ON UserIdentities(UserId);

-- The feed itself. Fetch state and validators are feed properties.
CREATE TABLE Feeds (
    Id                  INTEGER PRIMARY KEY,
    Url                 TEXT NOT NULL UNIQUE,
    Title               TEXT,
    SiteUrl             TEXT,
    IconUrl             TEXT,
    ETag                TEXT,
    LastModified        TEXT,
    LastFetchedUtc      TEXT,
    NextFetchUtc        TEXT,
    IntervalSeconds     INTEGER,
    ConsecutiveFailures INTEGER NOT NULL DEFAULT 0,
    LastError           TEXT,
    CreatedUtc          TEXT NOT NULL
);
CREATE INDEX IX_Feeds_NextFetch ON Feeds(NextFetchUtc);

CREATE TABLE Subscriptions (
    UserId          INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    FeedId          INTEGER NOT NULL REFERENCES Feeds(Id) ON DELETE CASCADE,
    SubscribedUtc   TEXT NOT NULL,
    HideBeforeOrder INTEGER,          -- "clear posts" watermark; NULL = show all
    PRIMARY KEY (UserId, FeedId)
);
CREATE INDEX IX_Subscriptions_Feed ON Subscriptions(FeedId);

-- A tag belongs to a user. IsHidden replaces the old UserTagSettings table.
CREATE TABLE Tags (
    Id       INTEGER PRIMARY KEY,
    UserId   INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    Name     TEXT NOT NULL,
    IsHidden INTEGER NOT NULL DEFAULT 0,
    UNIQUE (UserId, Name)
);

CREATE TABLE SubscriptionTags (
    UserId INTEGER NOT NULL,
    FeedId INTEGER NOT NULL,
    TagId  INTEGER NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
    PRIMARY KEY (UserId, FeedId, TagId),
    FOREIGN KEY (UserId, FeedId) REFERENCES Subscriptions(UserId, FeedId) ON DELETE CASCADE
);
CREATE INDEX IX_SubscriptionTags_Tag ON SubscriptionTags(TagId, FeedId);

-- One row per post, ever.
CREATE TABLE Posts (
    Id               INTEGER PRIMARY KEY,
    FeedId           INTEGER NOT NULL REFERENCES Feeds(Id) ON DELETE CASCADE,
    Href             TEXT NOT NULL,
    CommentsHref     TEXT,
    Title            TEXT,
    PublishDateOrder INTEGER NOT NULL,
    PublishDate      TEXT,
    ThumbnailUrl     TEXT,
    FirstSeenUtc     TEXT NOT NULL,
    UNIQUE (FeedId, Href)
);
CREATE INDEX IX_Posts_Timeline     ON Posts(PublishDateOrder DESC, Id DESC);
CREATE INDEX IX_Posts_FeedTimeline ON Posts(FeedId, PublishDateOrder DESC, Id DESC);

CREATE TABLE PostContent (
    PostId  INTEGER PRIMARY KEY REFERENCES Posts(Id) ON DELETE CASCADE,
    Title   TEXT,                      -- duplicated from Posts so FTS indexes it
    Content TEXT
);

CREATE VIRTUAL TABLE Posts_fts USING fts5(
    Title, Content,
    content='PostContent', content_rowid='PostId'
);
-- Standard external-content triggers (insert / delete / update) on PostContent.

-- Sparse: a row exists only when something differs from "unread, not saved".
CREATE TABLE UserPostState (
    UserId   INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    PostId   INTEGER NOT NULL REFERENCES Posts(Id) ON DELETE CASCADE,
    IsRead   INTEGER NOT NULL DEFAULT 0,
    IsSaved  INTEGER NOT NULL DEFAULT 0,
    ReadUtc  TEXT,
    SavedUtc TEXT,
    PRIMARY KEY (UserId, PostId)
);
CREATE INDEX IX_UserPostState_Saved ON UserPostState(UserId, IsSaved) WHERE IsSaved = 1;

-- Unchanged.
CREATE TABLE SystemStats ( ... as today ... );
```

Superseded tables (renamed with a `Legacy_` prefix by the migration, dropped
by hand later): `Items`, `ItemContent`, `Items_fts`, `FeedValidators`,
`FeedFetchState`, `UserTagSettings`. Removed column: `IsPaywalled`
(no code path used it; the concept is dropped entirely).

## 4. Semantics

| Operation | Old | New |
|---|---|---|
| Add feed | Insert per-user Feeds row, fetch for that user | Upsert `Feeds` by URL, insert `Subscriptions`. If the feed row is new, schedule it for immediate fetch. Existing posts are visible at once. No per-user fetch. |
| Remove feed | Delete per-user row and that user's items | Delete the `Subscriptions` row (cascades tags). Posts stay. |
| Scheduled fetch | Fetch once, clone items per subscriber | Fetch once, `INSERT OR IGNORE` into `Posts` + `PostContent`. No fan-out. |
| Mark read / save | Update flags on the user's item row | Upsert `UserPostState`. Clearing both flags may delete the row. |
| Clear posts | Delete the user's item rows | Set `Subscriptions.HideBeforeOrder` to the current max `PublishDateOrder` for that feed (or all subscriptions for "clear all"). Reversible. |
| Delete account | Delete rows across six tables | `DELETE FROM Users`; everything per-user cascades. Shared posts untouched. |
| New subscriber sees | Only posts fetched after subscribing | All stored posts for the feed, all unread. |
| Hidden tags | Filter by `UserTagSettings` then exclude feed URLs | `Tags.IsHidden` joined through `SubscriptionTags`. |

### Timeline query

```sql
SELECT p.*, f.Url AS FeedUrl,
       COALESCE(s.IsRead, 0)  AS IsRead,
       COALESCE(s.IsSaved, 0) AS IsSaved
FROM Posts p
JOIN Feeds f           ON f.Id = p.FeedId
JOIN Subscriptions sub ON sub.FeedId = p.FeedId AND sub.UserId = @user
LEFT JOIN UserPostState s ON s.PostId = p.Id AND s.UserId = @user
WHERE (p.PublishDateOrder, p.Id) < (@cursorOrder, @cursorId)
  AND (sub.HideBeforeOrder IS NULL OR p.PublishDateOrder > sub.HideBeforeOrder)
  AND NOT EXISTS (                       -- hidden-tag exclusion
        SELECT 1 FROM SubscriptionTags st JOIN Tags t ON t.Id = st.TagId
        WHERE st.UserId = @user AND st.FeedId = p.FeedId AND t.IsHidden = 1)
  -- optional filters:
  --   unread:  AND COALESCE(s.IsRead, 0) = 0
  --   saved:   AND s.IsSaved = 1
  --   tag:     AND EXISTS (SELECT 1 FROM SubscriptionTags st JOIN Tags t ...
  --                        WHERE st.UserId=@user AND st.FeedId=p.FeedId AND t.Name=@tag)
  --   feed:    AND p.FeedId = @feedId
ORDER BY p.PublishDateOrder DESC, p.Id DESC
LIMIT @pageSize;
```

The planner walks `IX_Posts_Timeline` newest-first and stops after one page
of matches. `GetNewItemCountAsync` is the same WHERE with the cursor
comparison flipped and `COUNT(*)`. The old post-query `DistinctBy(Href)`
stays in the controller.

### Search

```sql
SELECT p.* ... FROM Posts_fts
JOIN Posts p ON p.Id = Posts_fts.rowid
JOIN Subscriptions sub ON sub.FeedId = p.FeedId AND sub.UserId = @user
WHERE Posts_fts MATCH @query
ORDER BY p.PublishDateOrder DESC LIMIT @pageSize OFFSET @offset;
```

Keep the existing `Title LIKE` fallback for partial words.

### Retention (new, nightly while a replica is up)

- Delete `Posts` older than N days (config, default 180) that have no
  `UserPostState` row with `IsSaved = 1`.
- Delete `Feeds` with zero subscriptions for more than 7 days.
- Both are small batched deletes under the write lock.

## 5. Code changes

HTTP routes, verbs and JSON shapes do not change. `NewsFeed` and
`NewsFeedItem` in `src/Shared` keep their fields and become the view the
repository assembles (`UserId` = requesting user, `FeedUrl` from `Feeds.Url`,
`Id` = `Posts.Id`, now shared across users and still opaque to the client).

Rewritten
- `SQLiteItemRepository`, `CachingItemRepository`
- `SQLiteFeedRepository`, `CachingFeedRepository`
- `SQLiteUserRepository` (identities table), `CachingUserRepository`

Removed (functionality moves into `Feeds`)
- `SQLiteFeedValidatorStore`, `SQLiteFeedScheduleStore`
- `IFeedValidatorStore`, `IFeedScheduleStore` either deleted and
  `FeedRefresher` takes `IFeedRepository`, or kept as thin adapters.
  Prefer deleting.

Interfaces
- `IItemRepository`: same method set. `DeleteAllItemsAsync` sets watermarks;
  `GetItemCountForFeed` counts visible shared posts.
- `IFeedRepository`: `Subscribe`, `Unsubscribe`, tag CRUD, `GetHiddenFeedIds`,
  `GetDueFeeds`, `RecordFetchResult`.
- `IUserRepository`: `FindByIdentity(provider, subject)`, `LinkIdentity`.

Edited
- `FeedRefresher`: remove `CloneForSubscriber`, the per-subscriber fan-out
  loop, `AddFeedAsync`'s forced fetch and the in-memory validator cache
  (`Feeds` row is the source of truth; keep the in-flight dedup and the
  per-host gate).
- `FeedController.AddFeedAsync`: no refresher call; subscribe only.
- `UserResolver`, `AuthenticationSetup.NormalizeIdentity`: resolve users via
  `UserIdentities`. The Microsoft email-hash link becomes an identity row
  with provider `email-link`.
- `RepositoryFactory`, `Program.cs`: wiring; add the retention hosted job.

Untouched
- `ItemController`, `UserController`, `AdminController`, auth endpoints,
  the WasmApp.

## 6. Tests

Testing is layered, and the layers land in this order so the migration PR is
measured against a safety net that is known to pass on `main` first.

### 6.1 API contract tests (separate PR, lands on `main` BEFORE the migration)

Today nothing exercises the HTTP layer: the suite is unit and repository
level, and the deploy smoke check is two curls. Since the redesign promises
that routes and JSON shapes do not change, that promise needs a test that
passes on the old code and must keep passing on the new.

- New test class(es) in `test/RssReaderTests` using
  `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory<Program>`),
  booting the real API in-process against a temp SQLite file, with the
  test-only header login in `ApiTestHost` (`X-Test-User`).
- Seed: two users; three feeds, one shared by both users; tags on both
  users' subscriptions, one hidden; a few dozen posts with mixed read/saved
  state and distinct `PublishDateOrder` values.
- For each endpoint assert status code and the JSON property names and
  types (not values that legitimately change, such as ids or timestamps):
  timeline first page and a cursor page; unread, saved and tag filters;
  per-feed items; feed list; add feed; mark read; save and unsave; tag
  update; search; new-item count; delete-all; OPML export and import;
  `/auth/me`; `/api/healthz`.
- Behavioural assertions that must hold before and after the redesign:
  read/saved state is per user; a hidden tag removes that feed's posts for
  that user only; cursor paging never repeats or skips a post; OPML export
  then import round-trips feeds and tags.
- One assertion that documents the bug being fixed and is expected to flip:
  user B adding a URL user A already has sees A's existing posts
  immediately. Mark it `[Ignore]` with the reason on `main`; the migration
  PR un-ignores it.
- Capture the expected shapes from the behaviour of `main`, not from the
  redesign code.

### 6.2 Unit and repository tests (in the migration PR)

- Repository tests on a temp SQLite file: subscribe shows existing posts;
  unsubscribe leaves posts; read/saved isolation between two users; hidden
  tag excludes a feed for one user only; watermark hides and un-hides;
  timeline cursor pagination order equals the old implementation on a seeded
  corpus; search respects subscriptions.
- Refresher tests with a stub `HttpMessageHandler`: one fetch per URL
  regardless of subscriber count; 304 records success and writes nothing;
  a new subscriber on an already-fetched feed triggers no HTTP call.
- Retention: saved posts survive; orphan feeds are removed after the grace
  period.
- Migration test: run the migration against a synthetic old-schema database
  and assert the section 7 count checks; also run it manually against a copy
  of the production file and record elapsed time and sizes in the PR.

### 6.3 Full-stack verification on real data (manual, before merge)

Copy `dbcopy\storage.db`, migrate the copy, run the Docker API plus the
Blazor app locally against it (`.github/skills/run-locally`), and check:
timeline loads and pages; unread filter; a per-feed page; search; mark read
persists across reload; save and unsave; a hidden tag hides its feed; OPML
export contains tags; sign-in works. Record the checklist and results in the
PR description.

### Sequencing

1. PR A: section 6.1 contract tests against `main`, green in CI, merged.
2. PR B: the redesign and migration, rebased on A, with 6.2 added and the
   6.1 suite still green (plus the one un-ignored test), and 6.3 recorded.

## 7. Migration and rollout

One PR through the ship loop. On first start after deploy the API runs a
one-shot migration before serving traffic. The app already scales to zero, so
a few minutes of unavailability on one deploy is acceptable.

### Mapping (old -> new), all deterministic

| New | Source |
|---|---|
| `Users` | copy `Id`, `Username`; `CreatedUtc` = now |
| `UserIdentities` | one row per non-null `ProviderUserId` (split `provider:subject`), `AadUserId` (`microsoft`), and the email-hash link column (`email-link`) |
| `Feeds` | `SELECT DISTINCT Url FROM Feeds`; `ETag`/`LastModified` from `FeedValidators` by URL; schedule columns from `FeedFetchState` by URL |
| `Subscriptions` | every old `Feeds` row -> (`UserId`, new `FeedId`); `SubscribedUtc` = now |
| `Tags` | split each old `Feeds.Tags` comma string, distinct per user; `IsHidden` from `UserTagSettings` |
| `SubscriptionTags` | the same split, re-keyed to `TagId` |
| `Posts` | `Items` grouped by (`FeedUrl`, `Href`), one representative row per group (`MIN(Id)`), re-keyed to `FeedId`; `FirstSeenUtc` = now |
| `PostContent` | `ItemContent` for the representative rows (fall back to `Items.Content` when missing) |
| `UserPostState` | every old `Items` row with `IsRead = 1` or `IsSaved = 1`, mapped through (`FeedUrl`, `Href`) to `PostId` |
| `Posts_fts` | rebuilt with `INSERT INTO Posts_fts(Posts_fts) VALUES('rebuild')` |

### Procedure

1. Guard: run only when the `Legacy_` tables do not exist and `Posts` is
   empty; record completion in a `SchemaMigrations` table so it never reruns.
2. Inside one transaction under the single-writer lock: create the new
   tables, run the mapping above in dependency order, verify counts (see
   below), then `ALTER TABLE ... RENAME TO Legacy_...` for every superseded
   table. Roll back the whole transaction on any failure and refuse to start.
3. `VACUUM` after commit, outside the transaction, to reclaim the duplicated
   content. Expect the file to shrink substantially.
4. Expose migration state on `/api/healthz` so the deploy validation sees it.

### Verification inside the migration

- `COUNT(DISTINCT FeedUrl, Href)` over old `Items` equals `COUNT(*)` over `Posts`.
- Old subscription rows equal new `Subscriptions` rows.
- Old read and saved row counts equal `UserPostState` rows with those flags.
- Per user, the first page of the new timeline matches the old one by `Href`
  order (sampled in tests on a seeded copy, not in production).

### Safety

- Before deploying, snapshot the Litestream container and copy the current
  database file. Rollback = redeploy the previous image against that copy.
- The `Legacy_` tables stay until the owner confirms the result, then are
  dropped by hand (`DROP TABLE Legacy_...; VACUUM;`).
- Item ids change: open clients reload from the top of the timeline once.
  Saved deep links to a post id, if any exist, break.
- Test the migration against a copy of the production file locally (the
  `dbcopy` folder holds one) before merging; record the elapsed time.

## 8. Decisions (approved 2026-10-23)

- Default retention window: 180 days for unsaved posts, configurable.
- OPML: verify that export and import still round-trip subscriptions with
  their tags on the new schema; it is the fallback if the migration must be
  abandoned. Fix it in the same PR if not.
- `UserIdentities` ships in the same PR, migrated from the existing identity
  columns. After deploy, verify sign-in with each provider first.
- Decided (second pass, 2026-10-23): migrate in place with no data loss
  rather than recreate empty. The mapping is deterministic and the cost is a
  few minutes of startup on one deploy.
- Tags: keep the id-keyed flat model. Hierarchy later = nullable `ParentId`
  on `Tags` plus a (user, parent, name) unique index; both additive.
