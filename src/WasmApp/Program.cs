using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RssApp.Config;
using RssApp.RssClient;
using WasmApp;
using WasmApp.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Add configuration from appsettings.json
builder.Configuration.AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true);

// Configure RssWasmConfig as a singleton with values from configuration
var config = RssWasmConfig.LoadFromAppSettings(builder.Configuration);
builder.Services
    .AddSingleton<RssWasmConfig>(_ => config)
    .AddTransient<IFeedClient, FeedClient>()
    .AddTransient<UserClient>();

var app = builder.Build();
await app.RunAsync();
