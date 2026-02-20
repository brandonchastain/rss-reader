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

        public static RssAppConfig LoadFromAppSettings(IConfiguration configuration)
        {
            var config = new RssAppConfig();
            configuration.GetSection(nameof(RssAppConfig)).Bind(config);
            return config;
        }
    }
}