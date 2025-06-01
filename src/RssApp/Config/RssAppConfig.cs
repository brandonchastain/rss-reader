using System;

namespace RssApp.Config
{
    public class RssAppConfig
    {
        public string UserDb { get; set; }
        public string ItemDb { get; set; }
        public string FeedDb { get; set; }
        public string AuthDb { get; set; }
        public bool IsTestUserEnabled { get; set; }
        public TimeSpan CacheReloadInterval { get; set; }
        public TimeSpan CacheReloadStartupDelay { get; set; }

        public static RssAppConfig LoadFromEnvironment()
        {
            const string userDbVar = "RSS_BC_USER_DB";
            const string feedDbVar = "RSS_BC_FEED_DB";
            const string itemDbVar = "RSS_BC_ITEM_DB";
            const string authDbVar = "RSS_BC_AUTH_DB";
            const string testUserEnabledVar = "RSS_BC_ENABLE_TEST_USER";
            const string cacheReloadIntervalMinsVar = "RSS_BC_CACHE_RELOAD_INTERVAL";
            const string cacheReloadStartupDelayMinsVar = "RSS_BC_CACHE_STARTUP_DELAY";

            return new RssAppConfig
            {
                UserDb = Environment.GetEnvironmentVariable(userDbVar) ?? "C:\\home\\data\\storage.db",
                ItemDb = Environment.GetEnvironmentVariable(itemDbVar) ?? "C:\\home\\data\\storage.db",
                FeedDb = Environment.GetEnvironmentVariable(feedDbVar) ?? "C:\\home\\data\\storage.db",
                AuthDb = Environment.GetEnvironmentVariable(authDbVar) ?? "C:\\home\\data\\auth.db",
                IsTestUserEnabled = bool.TryParse(Environment.GetEnvironmentVariable(testUserEnabledVar), out var isEnabled) && isEnabled,
                CacheReloadInterval = TimeSpan.FromMinutes(int.TryParse(Environment.GetEnvironmentVariable(cacheReloadIntervalMinsVar), out var interval) ? interval : 60),
                CacheReloadStartupDelay = TimeSpan.FromMinutes(int.TryParse(Environment.GetEnvironmentVariable(cacheReloadStartupDelayMinsVar), out var delay) ? delay : 10)
            };
        }
    }
}