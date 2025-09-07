using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RssApp.Config;
using RssApp.RssClient;
using WasmApp;
using WasmApp.Services;
using System;
// using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

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
    .AddTransient<IUserClient, UserClient>();

// Central HttpClient factory registration
builder.Services
    .AddHttpClient("api", client =>
    {
        client.BaseAddress = new Uri(config.ApiBaseUrl);
    });

builder.Services
    .AddHttpClient("refresh", client =>
    {
        client.BaseAddress = new Uri(config.ApiBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
    });

builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, MyAuthenticationStateProvider>();

var app = builder.Build();
await app.RunAsync();
