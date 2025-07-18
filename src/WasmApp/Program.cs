using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RssApp.Config;
using RssApp.RssClient;
using WasmApp;
using WasmApp.Services;

var config = RssWasmConfig.LoadFromEnvironment();
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
    .AddScoped(sp => new HttpClient { BaseAddress = new Uri(config.ApiBaseUrl) })
    .AddTransient<IFeedClient, FeedClient>();

builder.Services.AddOidcAuthentication(options =>
{
    var azureAd = builder.Configuration.GetSection("AzureAd");
    options.ProviderOptions.Authority = azureAd["Authority"];
    options.ProviderOptions.ClientId = azureAd["ClientId"];
    options.ProviderOptions.DefaultScopes.Add("openid");
    options.ProviderOptions.DefaultScopes.Add("profile");
});

await builder.Build().RunAsync();
