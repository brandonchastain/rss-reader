
namespace RssApp.Config
{
    public class RssWasmConfig
    {
        private RssWasmConfig()
        {
        }

        public string ApiBaseUrl { get; set; } = "https://localhost:7034";

        public static RssWasmConfig LoadFromEnvironment()
        {
            return new RssWasmConfig
            {
                ApiBaseUrl = Environment.GetEnvironmentVariable("RSS_WASM_API_BASE_URL") ?? "https://localhost:7034"
            };
        }
    }
}