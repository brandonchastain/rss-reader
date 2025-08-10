
using Microsoft.Extensions.Configuration;

namespace RssApp.Config
{
    public class RssWasmConfig
    {
        public RssWasmConfig()
        {
        }

        public string ApiBaseUrl { get; set; } = "https://localhost:7034/";

        public string AuthApiBaseUrl { get; set; } = "https://localhost:7085/";

        public static RssWasmConfig LoadFromAppSettings(IConfiguration configuration)
        {
            var config = new RssWasmConfig();
            configuration.GetSection(nameof(RssWasmConfig)).Bind(config);
            return config;
        }
    }
}