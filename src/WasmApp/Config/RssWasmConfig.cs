
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

        /// <summary>Legacy: sign in through Static Web Apps Easy Auth (/.auth/*) instead of the API's /auth/*.</summary>
        public bool UseStaticWebAppsAuth { get; set; } = false;

        public static RssWasmConfig LoadFromAppSettings(IConfiguration configuration)
        {
            var config = new RssWasmConfig();
            configuration.GetSection(nameof(RssWasmConfig)).Bind(config);
            return config;
        }
    }
}
