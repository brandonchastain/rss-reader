using Microsoft.Extensions.Caching.Memory;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Data;
using RssApp.Filters;
using RssApp.RssClient;
using RssApp.Serialization;
using RssReader.Server.Services;
using Server.Authentication;

// Add configuration from appsettings.json
var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables();
var config = RssAppConfig.LoadFromAppSettings(builder.Configuration);
// Readers use Mode=ReadOnly to avoid SQLite WAL/SHM conflicts with Litestream follow mode.
// Writers use ReadWriteCreate with Cache=Shared for normal operation.
string dbConnectionString = config.IsReadOnly
    ? $"Data Source={config.DbLocation};Mode=ReadOnly;Pooling=True"
    : $"Data Source={config.DbLocation};Mode=ReadWriteCreate;Cache=Shared;Pooling=True";

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxConcurrentConnections = 50;
    serverOptions.Limits.MaxConcurrentUpgradedConnections = 50;
    serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
});

builder.Services.AddMemoryCache();
builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:8443", "https://localhost:8443")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// Add authn and authz
builder.Services
    .AddAuthorization()
    .AddAuthentication(StaticWebAppsAuthenticationHandler.AuthenticationScheme)
    .AddScheme<StaticWebAppsAuthenticationOptions, StaticWebAppsAuthenticationHandler>(
        StaticWebAppsAuthenticationHandler.AuthenticationScheme,
        options =>
        {
            options.IsTestUserEnabled = config.IsTestUserEnabled;
        });

// Register services for DI
// Repositories: SQLite implementations wrapped with caching decorators.
// Creation order matters — feed and user repos must exist before item repo.
builder.Services
    .AddSingleton<RssAppConfig>(_ => config)
    .AddSingleton<RepositoryFactory>(sb => new RepositoryFactory(dbConnectionString, sb, config.IsReadOnly))
    .AddSingleton<IFeedRepository>(sb =>
    {
        var inner = sb.GetRequiredService<RepositoryFactory>().CreateFeedRepository();
        return new CachingFeedRepository(inner, sb.GetRequiredService<IMemoryCache>());
    })
    .AddSingleton<IUserRepository>(sb =>
    {
        var inner = sb.GetRequiredService<RepositoryFactory>().CreateUserRepository();
        return new CachingUserRepository(inner, sb.GetRequiredService<IMemoryCache>());
    })
    .AddSingleton<IItemRepository>(sb =>
    {
        var inner = sb.GetRequiredService<RepositoryFactory>().CreateItemRepository();
        return new CachingItemRepository(inner, sb.GetRequiredService<IMemoryCache>());
    })
    .AddSingleton<IUserResolver, UserResolver>()
    .AddSingleton<FeedThumbnailRetriever>();

if (!config.IsReadOnly)
{
    // Write-mode services: background feed refresh, database backup, RSS fetching
    builder.Services
        .AddSingleton<RssDeserializer>()
        .AddSingleton<BackgroundWorkQueue>()
        .AddSingleton<DatabaseBackupService>()
        .AddHostedService(p => p.GetRequiredService<DatabaseBackupService>())
        .AddHostedService<BackgroundWorker>()
        .AddSingleton<IFeedRefresher, FeedRefresher>()
        .AddTransient<RedirectDowngradeHandler>()
        .AddHttpClient("RssClient")
        .AddHttpMessageHandler<RedirectDowngradeHandler>()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
}
else
{
    // Read-only mode: register no-op IFeedRefresher so controllers can still resolve it
    builder.Services.AddSingleton<IFeedRefresher, NoOpFeedRefresher>();
}

builder.Services.AddControllers(options =>
{
    if (config.IsReadOnly)
    {
        options.Filters.Add<ReadOnlyActionFilter>();
    }
});

if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseKestrel();
}

var app = builder.Build();

// Restore database from backup (writer mode only — readers use Litestream restore)
if (!config.IsReadOnly)
{
    var backup = app.Services.GetRequiredService<DatabaseBackupService>();
    await backup.RestoreFromBackupAsync(CancellationToken.None);
}

// Writer: instantiate repos to create database tables in order.
// Reader: repos are still instantiated (singletons), but skip schema init
// because the DB is restored from Litestream with tables already in place.
var a = app.Services.GetRequiredService<IFeedRepository>();
var b = app.Services.GetRequiredService<IUserRepository>();
var c = app.Services.GetRequiredService<IItemRepository>();

// After schema init (writer) or startup (reader), enable PRAGMA query_only
// on all subsequent connections. This is the DB-level backstop — even if a
// write request bypasses the HTTP filter, SQLite will reject the mutation.
if (config.IsReadOnly)
{
    DatabaseMode.EnableQueryOnly();
}

// Enable middleware 
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseCors("LocalFrontend");
app.UseAuthentication();
app.UseAuthorization();

// Setup endpoints
app.MapControllers();
app.MapGet("/api/healthz", () => Results.Ok(new { status = "healthy", role = config.IsReadOnly ? "reader" : "writer" })).AllowAnonymous();

if (config.IsTestUserEnabled)
{
    app.MapGet("/.auth/me", (HttpContext context) =>
    {
        return Results.Json(new
        {
            clientPrincipal = new
            {
                identityProvider = "test",
                userId = "testuser2",
                userDetails = "testuser2",
                userRoles = new string[] { "authenticated", "admin" }
            }
        });
    }).AllowAnonymous();
}

// Start the application
app.Run();
