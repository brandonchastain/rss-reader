using System;

namespace RssApp.Config
{
    public class RssAppConfig
    {
        public string ServerHostName { get; set; } = "https://localhost:8080/";
        public string DbLocation { get; set; }
        public bool IsTestUserEnabled { get; set; }

        // Persistent backup file path on the Azure Files mount (or local disk in dev).
        // Empty string disables the backup-to-file service entirely.
        // The parent directory must exist and be writable; otherwise the writer
        // entrypoint and the BackgroundService will fail fast.
        public string BackupDbPath { get; set; } = string.Empty;
        public TimeSpan BackupInterval { get; set; } = TimeSpan.FromMinutes(5);

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

        public bool RebuildFtsOnStartup { get; set; }

        public string AdminAadUserIds { get; set; } = string.Empty;

        public static RssAppConfig LoadFromAppSettings(IConfiguration configuration)
        {
            var config = new RssAppConfig();
            configuration.GetSection(nameof(RssAppConfig)).Bind(config);
            return config;
        }
    }
}