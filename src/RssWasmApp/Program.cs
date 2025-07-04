using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RssApp.ComponentServices;
using RssApp.Data;
using RssApp.RssClient;
using RssApp.Serialization;
using RssWasmApp;
using RssWasmApp.Mocks;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddSingleton<IUserRepository, MockUserRepository>()
    .AddSingleton<IFeedRepository, MockFeedRepository>()
    .AddSingleton<IItemRepository, MockItemRepository>()
    .AddSingleton<RssDeserializer>()
    .AddSingleton<IFeedRefresher, MockFeedRefresher>()
    .AddTransient<IFeedClient, MockFeedClient>()
    .AddSingleton<OpmlSerializer>();

builder.Services.AddOidcAuthentication(options =>
{
    var azureAd = builder.Configuration.GetSection("AzureAd");
    options.ProviderOptions.Authority = azureAd["Authority"];
    options.ProviderOptions.ClientId = azureAd["ClientId"];
    options.ProviderOptions.DefaultScopes.Add("openid");
    options.ProviderOptions.DefaultScopes.Add("profile");
});

await builder.Build().RunAsync();
