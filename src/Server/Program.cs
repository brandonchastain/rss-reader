using Microsoft.AspNetCore.Cors.Infrastructure;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Data;
using RssApp.RssClient;
using RssApp.Serialization;

var config = RssAppConfig.LoadFromEnvironment();
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxConcurrentConnections = 50;
    serverOptions.Limits.MaxConcurrentUpgradedConnections = 50;
    serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
});

var configureCors = (CorsOptions options) => 
{
    options.AddPolicy(
        name: "AllowSpecificOrigins",
        policy =>
        {
            policy
            .WithOrigins("*")
            .AllowAnyHeader()
            .AllowAnyMethod();
        });
};

builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
});

builder.Services.AddCors(configureCors);
builder.Services.AddControllers();

// RSS services
builder.Services
.AddSingleton<IUserRepository>(sb =>
{
    return new SQLiteUserRepository(
        $"Data Source={config.UserDb}",
        sb.GetRequiredService<ILogger<SQLiteUserRepository>>());
})
.AddSingleton<IFeedRepository>(sb =>
{
    return new SQLiteFeedRepository(
        $"Data Source={config.FeedDb}",
        sb.GetRequiredService<ILogger<SQLiteFeedRepository>>());
})
.AddSingleton<IItemRepository>(sb =>
{
    return new SQLiteItemRepository(
        $"Data Source={config.ItemDb}",
        sb.GetRequiredService<ILogger<SQLiteItemRepository>>(),
        sb.GetRequiredService<IFeedRepository>(),
        sb.GetRequiredService<IUserRepository>());
})
.AddSingleton<RssDeserializer>()
.AddSingleton<BackgroundWorkQueue>()
.AddHostedService<BackgroundWorker>()
.AddSingleton<IFeedRefresher>(sp =>
{
    return new FeedRefresher(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<RssDeserializer>(),
        sp.GetRequiredService<ILogger<FeedRefresher>>(),
        sp.GetRequiredService<IFeedRepository>(),
        sp.GetRequiredService<IItemRepository>(),
        sp.GetRequiredService<IUserRepository>(),
        sp.GetRequiredService<BackgroundWorkQueue>(),
        cacheReloadInterval: TimeSpan.FromMinutes(5),
        cacheReloadStartupDelay: TimeSpan.FromSeconds(10));
})
.AddTransient<RedirectDowngradeHandler>()
.AddHttpClient("RssClient")
.AddHttpMessageHandler<RedirectDowngradeHandler>()
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseDefaultCredentials = true
});

// Configure HTTPS for prod
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddLettuceEncrypt();
    builder.WebHost.UseKestrel(k =>
    {
        var appServices = k.ApplicationServices;
        k.ConfigureHttpsDefaults(h =>
        {
            h.UseLettuceEncrypt(appServices);
        });
    });
}

var app = builder.Build();

// Instantiate repositories to ensure db tables are created in order.
var a = app.Services.GetRequiredService<IFeedRepository>();
var b = app.Services.GetRequiredService<IUserRepository>();
var c = app.Services.GetRequiredService<IItemRepository>();

app.UseHttpsRedirection();
app.UseCors("AllowSpecificOrigins");
// app.UseAuthorization();
app.MapControllers();
app.Run();