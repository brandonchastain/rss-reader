using System;

namespace RssApp.Config
{
    public class RssAppConfig
    {
        public string ServerHostName { get; set; } = "https://localhost:8080/";
        public string DbLocation { get; set; }
        public bool IsTestUserEnabled { get; set; }
        public TimeSpan CacheReloadInterval { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan CacheReloadStartupDelay { get; set; } = TimeSpan.FromSeconds(0);
        public bool IsReadOnly { get; set; }

        // Keep at 1 to avoid SQLite write contention. Multiple background workers
        // compete for the single-writer lock during feed refresh, causing SQLITE_BUSY
        // under load. Feed refresh is I/O-bound (HTTP fetch), so 1 worker still
        // saturates outbound bandwidth. The real fix for refresh speed is incremental
        // signaling (FeedRefresher per-user state), not parallelism.
        public int BackgroundWorkerCount { get; set; } = 1;
        public int BackgroundQueueCapacity { get; set; } = 1000;
        public bool RebuildFtsOnStartup { get; set; }

        public static RssAppConfig LoadFromAppSettings(IConfiguration configuration)
        {
            var config = new RssAppConfig();
            configuration.GetSection(nameof(RssAppConfig)).Bind(config);
            return config;
        }
    }
}