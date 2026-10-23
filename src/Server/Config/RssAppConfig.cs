using System;

namespace RssApp.Config
{
    /// <summary>
    /// Server settings, bound from the RssAppConfig section of appsettings.json or RssAppConfig__* environment
    /// variables. TimeSpan values use "d.hh:mm:ss" (for example "00:15:00").
    /// </summary>
    public class RssAppConfig
    {
        // Storage

        /// <summary>Path of the live SQLite database file.</summary>
        public string DbLocation { get; set; }

        /// <summary>Serve reads only and refuse every write; for read replicas.</summary>
        public bool IsReadOnly { get; set; }

        /// <summary>Rebuild the full-text search index from stored posts at startup.</summary>
        public bool RebuildFtsOnStartup { get; set; }

        // Backup (durable copy of the database on a mounted share)

        /// <summary>Path of the backup copy on the share; empty disables backups and the startup handover.</summary>
        public string BackupDbPath { get; set; } = string.Empty;

        /// <summary>Minimum time between periodic backups; a backup only runs if the database changed.</summary>
        public TimeSpan BackupInterval { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>Age after which the writer lock file is treated as abandoned and taken over.</summary>
        public TimeSpan BackupLockStaleAfter { get; set; } = TimeSpan.FromMinutes(3);

        /// <summary>Time limit for the final backup on shutdown; must fit inside the container's termination grace period.</summary>
        public TimeSpan ShutdownBackupTimeout { get; set; } = TimeSpan.FromSeconds(90);

        // Feed fetching

        /// <summary>Run the background scheduler that fetches each distinct feed once on its own cadence.</summary>
        public bool SchedulerEnabled { get; set; } = true;

        /// <summary>Longest the scheduler sleeps between passes when nothing is due.</summary>
        public TimeSpan SchedulerTickInterval { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>Delay after startup before the first scheduler pass, so a cold start serves its first request first.</summary>
        public TimeSpan SchedulerStartupDelay { get; set; } = TimeSpan.FromSeconds(45);

        /// <summary>Polling interval for feeds that do not advertise one.</summary>
        public TimeSpan FeedRefreshInterval { get; set; } = TimeSpan.FromHours(1);

        /// <summary>Shortest polling interval honoured, whatever a feed advertises.</summary>
        public TimeSpan FeedRefreshIntervalFloor { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>Longest polling interval honoured, whatever a feed advertises.</summary>
        public TimeSpan FeedRefreshIntervalMax { get; set; } = TimeSpan.FromHours(6);

        /// <summary>Feeds fetched and parsed concurrently during one refresh.</summary>
        public int RefreshFetchConcurrency { get; set; } = 8;

        /// <summary>Simultaneous in-flight fetches to any single origin host.</summary>
        public int MaxConcurrentFetchesPerHost { get; set; } = 2;

        /// <summary>First retry delay after a failed fetch; doubles with each consecutive failure.</summary>
        public TimeSpan FeedBackoffBase { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>Ceiling for the failed-fetch retry delay.</summary>
        public TimeSpan FeedBackoffMax { get; set; } = TimeSpan.FromHours(6);

        // Background work

        /// <summary>Workers running queued jobs; keep at 1 because SQLite allows a single writer.</summary>
        public int BackgroundWorkerCount { get; set; } = 1;

        /// <summary>Maximum jobs waiting in the background queue.</summary>
        public int BackgroundQueueCapacity { get; set; } = 1000;

        /// <summary>Cooldown between manual refreshes by one user.</summary>
        public TimeSpan CacheReloadInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Manual refreshes are ignored for this long after startup.</summary>
        public TimeSpan CacheReloadStartupDelay { get; set; } = TimeSpan.FromSeconds(0);

        // Retention

        /// <summary>Delete unsaved posts first seen longer ago than this many days; 0 disables the post sweep.</summary>
        public int PostRetentionDays { get; set; } = 180;

        /// <summary>Delete a feed (and its posts) this many days after its last subscriber leaves, unless a post is saved.</summary>
        public int FeedOrphanGraceDays { get; set; } = 7;

        /// <summary>Time between retention sweeps.</summary>
        public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(24);

        /// <summary>Delay after startup before the first retention sweep.</summary>
        public TimeSpan RetentionStartupDelay { get; set; } = TimeSpan.FromMinutes(10);

        // Access

        /// <summary>Authenticate every request as a fake administrator; refused in Production unless AllowTestUserInProduction is set.</summary>
        public bool IsTestUserEnabled { get; set; }

        /// <summary>Permit IsTestUserEnabled in Production, for a private single-user box with no login.</summary>
        public bool AllowTestUserInProduction { get; set; }

        /// <summary>Comma-separated login ids (provider:subject, or a bare subject) that are administrators.</summary>
        public string AdminAadUserIds { get; set; } = string.Empty;

        /// <summary>Sign-in providers, session cookie, and allowed frontend origins.</summary>
        public AuthConfig Auth { get; set; } = new AuthConfig();

        public static RssAppConfig LoadFromAppSettings(IConfiguration configuration)
        {
            var config = new RssAppConfig();
            configuration.GetSection(nameof(RssAppConfig)).Bind(config);
            return config;
        }
    }
}
