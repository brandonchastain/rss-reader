
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
                //TODO figure out how to configure this in blazor wasm with static web apps
                // (env vars not supported in blazorwasm)
                ApiBaseUrl = Environment.GetEnvironmentVariable("RSS_WASM_API_BASE_URL") ?? "https://rssreader.brandonchastain.com/",
            };
        }
    }
}