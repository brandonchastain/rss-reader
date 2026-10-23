using System;

namespace RssApp.Config
{
    public class RssAppConfig
    {
        public string DbLocation { get; set; }
        public bool IsTestUserEnabled { get; set; }

        // Persistent backup file path on the Azure Files mount (or local disk in dev).
        // Empty string disables the backup-to-file service entirely.
        // The parent directory must exist and be writable; otherwise the writer
        // entrypoint and the BackgroundService will fail fast.
        public string BackupDbPath { get; set; } = string.Empty;
        // The backup only runs when the DB changed since the last one, so with hourly
        // feed polling this is mostly the cap on how often an active reading session
        // (every mark-read bumps the DB) triggers a full VACUUM INTO + copy to the
        // share. 15 min keeps that work down; read-state is all a crash can lose.
        public TimeSpan BackupInterval { get; set; } = TimeSpan.FromMinutes(15);
        // The writer holds a lock file next to BackupDbPath for its whole lifetime and
        // refreshes its mtime every 30 s. A booting revision waits for that lock so it
        // seeds from the old revision's final copy; a lock older than this belongs to a
        // replica that died without releasing it and is taken over. Keep it a small
        // multiple of the heartbeat: it is the worst-case boot delay after a crash.
        public TimeSpan BackupLockStaleAfter { get; set; } = TimeSpan.FromMinutes(3);
        // Upper bound for the final copy to the share on SIGTERM. Must fit, with the
        // host's shutdown timeout, inside terminationGracePeriodSeconds in main.bicep.
        public TimeSpan ShutdownBackupTimeout { get; set; } = TimeSpan.FromSeconds(90);

        public TimeSpan CacheReloadInterval { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan CacheReloadStartupDelay { get; set; } = TimeSpan.FromSeconds(0);
        public bool IsReadOnly { get; set; }

        // Keep at 1 to avoid SQLite write contention. Multiple background workers
        // compete for the single-writer lock during feed refresh, causing SQLITE_BUSY
        // under load. A refresh now runs as a single queued job that fans out the
        // HTTP fetches itself (see RefreshFetchConcurrency) while DB writes stay
        // serialized through the item repository's write semaphore.
        public int BackgroundWorkerCount { get; set; } = 1;
        public int BackgroundQueueCapacity { get; set; } = 1000;

        // How many feeds to fetch+parse concurrently within a single refresh.
        // Feed refresh is dominated by external HTTP latency, so fanning the
        // fetches out turns wall-clock from sum-of-feeds into roughly
        // slowest-feed * ceil(feeds / concurrency). DB writes remain serialized,
        // so this does not increase SQLite write contention.
        public int RefreshFetchConcurrency { get; set; } = 8;

        // Max simultaneous in-flight fetches to a single origin host, across all
        // users' concurrent refreshes. Keeps a popular host (one many users
        // subscribe to) from receiving the full RefreshFetchConcurrency burst at
        // once. Bounds politeness per-host independently of the global gate.
        public int MaxConcurrentFetchesPerHost { get; set; } = 2;

        // Exponential backoff applied to a single feed URL after a failed or
        // non-OK fetch (network error, timeout, 5xx, or 429/503 without a usable
        // Retry-After). Delay grows base * 2^(failures-1), capped at Max, with
        // jitter. An explicit Retry-After header always takes precedence. State
        // is in-memory only (resets on restart), matching the validator cache.
        public TimeSpan FeedBackoffBase { get; set; } = TimeSpan.FromMinutes(2);
        public TimeSpan FeedBackoffMax { get; set; } = TimeSpan.FromHours(6);

        // Background feed scheduler: fetches each DISTINCT feed URL on a cadence
        // (decoupled from user refreshes) and fans new items out to every
        // subscriber, so a feed shared by N users is fetched once, not N times.
        // Set false to fall back to purely user-triggered refreshes.
        public bool SchedulerEnabled { get; set; } = true;

        // Upper bound on how long the scheduler sleeps between passes. The scheduler
        // normally sleeps exactly until the earliest feed is next due, so this only
        // matters when nothing is scheduled or a pass fails. Kept at the feed
        // interval floor so a bad schedule can never delay a feed by more than one
        // extra cycle.
        public TimeSpan SchedulerTickInterval { get; set; } = TimeSpan.FromMinutes(30);

        // How long after startup to hold off the first scheduler tick. The replica
        // runs on a fraction of a vCPU and scales from zero, so a cold start's first
        // user request lands the moment the startup probe passes. Fanning out feed
        // fetches before then contends for that CPU and slows the request that woke
        // the container in the first place.
        public TimeSpan SchedulerStartupDelay { get; set; } = TimeSpan.FromSeconds(45);

        // Polling interval used when a feed advertises no <ttl> / sy:updatePeriod.
        // Hourly is what most hosted readers do; fetch work is the replica's main
        // active-time cost, so this is the biggest single lever on the bill.
        public TimeSpan FeedRefreshInterval { get; set; } = TimeSpan.FromHours(1);

        // Clamp for feed-advertised intervals: never poll more often than the floor
        // (politeness) nor wait longer than the max (freshness).
        public TimeSpan FeedRefreshIntervalFloor { get; set; } = TimeSpan.FromMinutes(30);
        public TimeSpan FeedRefreshIntervalMax { get; set; } = TimeSpan.FromHours(6);

        public bool RebuildFtsOnStartup { get; set; }

        // Retention (DB-REDESIGN-SPEC.md section 4): posts are shared, so the
        // only thing that removes them is this nightly job. A post first seen
        // more than PostRetentionDays ago is deleted unless some user saved
        // it; a feed nobody has subscribed to for FeedOrphanGraceDays goes too
        // (with its posts), unless one of them is saved. 0 days disables the
        // post sweep.
        public int PostRetentionDays { get; set; } = 180;
        public int FeedOrphanGraceDays { get; set; } = 7;
        public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(24);
        public TimeSpan RetentionStartupDelay { get; set; } = TimeSpan.FromMinutes(10);

        public string AdminAadUserIds { get; set; } = string.Empty;

        // Login: providers, session cookie, allowed frontend origins. See AuthConfig.
        public AuthConfig Auth { get; set; } = new AuthConfig();

        public static RssAppConfig LoadFromAppSettings(IConfiguration configuration)
        {
            var config = new RssAppConfig();
            configuration.GetSection(nameof(RssAppConfig)).Bind(config);
            return config;
        }
    }
}