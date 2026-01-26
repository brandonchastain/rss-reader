using System;

namespace RssApp.Config
{
    public class RssAppConfig
    {
        public string ServerHostName { get; set; } = "https://localhost:7034/";
        public string UserDb { get; set; }
        public string ItemDb { get; set; }
        public string FeedDb { get; set; }
        public bool IsTestUserEnabled { get; set; }

        public static RssAppConfig LoadFromAppSettings(IConfiguration configuration)
        {
            var config = new RssAppConfig();
            configuration.GetSection(nameof(RssAppConfig)).Bind(config);
            return config;
        }
    }
}