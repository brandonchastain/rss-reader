
namespace RssApp.Config
{
    public class RssWasmConfig
    {
        public RssWasmConfig()
        {
        }

        public string ApiBaseUrl { get; set; } = "https://localhost:8080/";
        public bool EnableTestAuth { get; set; } = false;

        // Migration window only: keep logging in through Static Web Apps Easy
        // Auth (/.auth/* on the page's origin) instead of the API's /auth/*.
        // Production sets this true until the cutover PR flips it; delete
        // afterwards together with AuthClient's legacy branch.
        public bool UseStaticWebAppsAuth { get; set; } = false;
        public string TestAuthUsername { get; set; } = "testuser";

        public static RssWasmConfig LoadFromAppSettings(IConfiguration configuration)
        {
            var config = new RssWasmConfig();
            configuration.GetSection(nameof(RssWasmConfig)).Bind(config);
            return config;
        }
    }
}