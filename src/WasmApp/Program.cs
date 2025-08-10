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
    .AddSingleton<RssWasmConfig>(_ => config)
    .AddTransient<IFeedClient, FeedClient>()
    .AddTransient<UserClient>();

var app = builder.Build();
await app.RunAsync();
