using Microsoft.AspNetCore.Cors.Infrastructure;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Data;
using RssApp.RssClient;
using RssApp.Serialization;
using RssReader.Server.Services;
using Server.Authentication;

var builder = WebApplication.CreateBuilder(args);

// Add configuration from appsettings.json
builder.Configuration.AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true);    
var config = RssAppConfig.LoadFromAppSettings(builder.Configuration);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxConcurrentConnections = 50;
    serverOptions.Limits.MaxConcurrentUpgradedConnections = 50;
    serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
});

builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
});

builder.Services.AddControllers();

// Add authentication
builder.Services.AddAuthentication(StaticWebAppsAuthenticationHandler.AuthenticationScheme)
    .AddScheme<StaticWebAppsAuthenticationOptions, StaticWebAppsAuthenticationHandler>(
        StaticWebAppsAuthenticationHandler.AuthenticationScheme,
        options => { options.IsTestUserEnabled = config.IsTestUserEnabled; });

builder.Services.AddAuthorization();

// RSS services
builder.Services
.AddSingleton<RssAppConfig>(_ => config)
.AddSingleton<IUserRepository>(sb =>
{
    return new SQLiteUserRepository(
        $"Data Source={config.UserDb};Mode=ReadWriteCreate;Cache=Shared;Pooling=True",
        sb.GetRequiredService<ILogger<SQLiteUserRepository>>());
})
.AddSingleton<IFeedRepository>(sb =>
{
    return new SQLiteFeedRepository(
        $"Data Source={config.FeedDb};Mode=ReadWriteCreate;Cache=Shared;Pooling=True",
        sb.GetRequiredService<ILogger<SQLiteFeedRepository>>());
})
.AddSingleton<IItemRepository>(sb =>
{
    return new SQLiteItemRepository(
        $"Data Source={config.ItemDb};Mode=ReadWriteCreate;Cache=Shared;Pooling=True",
        sb.GetRequiredService<ILogger<SQLiteItemRepository>>(),
        sb.GetRequiredService<IFeedRepository>(),
        sb.GetRequiredService<IUserRepository>(),
        sb.GetRequiredService<FeedThumbnailRetriever>());
})
.AddSingleton<RssDeserializer>()
.AddSingleton<BackgroundWorkQueue>()
.AddHostedService<BackgroundWorker>()
.AddSingleton<DatabaseBackupService>()
.AddHostedService<DatabaseBackupService>(p => p.GetRequiredService<DatabaseBackupService>())
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
.AddSingleton<FeedThumbnailRetriever>()
.AddTransient<RedirectDowngradeHandler>()
.AddHttpClient("RssClient")
.AddHttpMessageHandler<RedirectDowngradeHandler>()
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseDefaultCredentials = true
});

if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseKestrel();
}

var app = builder.Build();

// Restore database from backup BEFORE building the app
var backup = app.Services.GetRequiredService<DatabaseBackupService>();
await backup.RestoreFromBackupAsync(CancellationToken.None);

// Instantiate repositories to ensure db tables are created in order.
var a = app.Services.GetRequiredService<IFeedRepository>();
var b = app.Services.GetRequiredService<IUserRepository>();
var c = app.Services.GetRequiredService<IItemRepository>();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();