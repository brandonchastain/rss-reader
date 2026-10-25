
namespace RssApp.Config
{
    /// <summary>Frontend settings, bound from the RssWasmConfig section of wwwroot/appsettings.json.</summary>
    public class RssWasmConfig
    {
        public RssWasmConfig()
        {
        }

        /// <summary>Base URL of the API, with a trailing slash.</summary>
        public string ApiBaseUrl { get; set; } = "https://localhost:8080/";

        public static RssWasmConfig LoadFromAppSettings(IConfiguration configuration)
        {
            var config = new RssWasmConfig();
            configuration.GetSection(nameof(RssWasmConfig)).Bind(config);
            return config;
        }
    }
}
