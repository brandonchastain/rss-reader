using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
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
var dbConnections = new SqliteDbConnections(config.DbLocation, config.IsReadOnly);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxConcurrentConnections = 500;
    serverOptions.Limits.MaxConcurrentUpgradedConnections = 500;
    serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
});

builder.Services.AddMemoryCache();
// Base IHttpClientFactory for all roles (readers included) so services like
// FaviconService resolve. The writer additionally configures the named
// "RssClient" below; on readers CreateClient("RssClient") falls back to a default.
builder.Services.AddHttpClient();
// Outbound destination policy. Registered for every role: the feed controller
// (present on readers too) validates a new feed's URL before storing it.
builder.Services.AddSingleton<OutboundUrlGuard>();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
});

// The frontend is a different origin and sends the session cookie, so the
// allowed origins are explicit and credentials are allowed.
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(config.Auth.AllowedFrontendOrigins.Select(ReturnUrlValidator.NormalizeOrigin).ToArray())
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Behind Container Apps ingress the app sees plain http; the OIDC redirect_uri
// and the Secure cookie need the original scheme and host.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add authn and authz: session cookie + one scheme per configured provider,
// plus the Static Web Apps header scheme during the migration window.
builder.Services.AddRssAuthentication(config);

// Register services for DI
// Repositories: SQLite implementations wrapped with caching decorators.
// Creation order matters — feed and user repos must exist before item repo.
builder.Services
    .AddSingleton<RssAppConfig>(_ => config)
    .AddSingleton<IDbConnections>(_ => dbConnections)
    .AddSingleton<RepositoryFactory>(sb => new RepositoryFactory(dbConnections, sb, config.IsReadOnly, config.RebuildFtsOnStartup))
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
    .AddSingleton<ItemsChangeStamp>()
    .AddSingleton<IItemRepository>(sb =>
    {
        var inner = sb.GetRequiredService<RepositoryFactory>().CreateItemRepository();
        return new CachingItemRepository(inner, sb.GetRequiredService<IMemoryCache>(), sb.GetRequiredService<ItemsChangeStamp>());
    })
    .AddSingleton<IUserResolver, UserResolver>()
    .AddSingleton(new DatabaseBackupPaths())
    .AddSingleton<FaviconService>()
    .AddSingleton<ThumbnailResolver>()
    .AddSingleton<ISystemStatsRepository>(sb =>
        new SQLiteSystemStatsRepository(dbConnections))
    .AddSingleton<SystemStatsCollector>();

if (!config.IsReadOnly)
{
    // Write-mode services: background feed refresh, database backup, RSS fetching
    builder.Services
        .AddSingleton<RssDeserializer>()
        .AddSingleton<BackgroundWorkQueue>()
        .AddSingleton<DatabaseBackupService>(sb =>
            new DatabaseBackupService(
                sb.GetRequiredService<ILogger<DatabaseBackupService>>(),
                sb.GetRequiredService<DatabaseBackupPaths>(),
                sb))
        .AddHostedService(p => p.GetRequiredService<DatabaseBackupService>())
        .AddSingleton<DatabaseBackupToFileService>(sb =>
            new DatabaseBackupToFileService(
                sb.GetRequiredService<ILogger<DatabaseBackupToFileService>>(),
                new DatabaseBackupToFilePaths(
                    ActiveDbPath: config.DbLocation,
                    BackupDbPath: config.BackupDbPath ?? string.Empty,
                    Interval: config.BackupInterval,
                    LockStaleThreshold: TimeSpan.FromMinutes(15))))
        .AddHostedService(p => p.GetRequiredService<DatabaseBackupToFileService>())
        .AddHostedService<BackgroundWorker>()
        .AddSingleton<IFeedValidatorStore>(sb =>
            sb.GetRequiredService<RepositoryFactory>().CreateFeedValidatorStore())
        .AddSingleton<IFeedScheduleStore>(sb =>
            sb.GetRequiredService<RepositoryFactory>().CreateFeedScheduleStore())
        .AddSingleton<IFeedRefresher, FeedRefresher>()
        .AddHostedService<FeedScheduler>()
        .AddTransient<RedirectDowngradeHandler>()
        .AddTransient<OutboundUrlGuardHandler>()
        // Per-request timeout so a single slow/hung feed can't stall the whole
        // parallel refresh batch (the default HttpClient timeout is 100s).
        .AddHttpClient("RssClient", c => c.Timeout = TimeSpan.FromSeconds(20))
        .AddHttpMessageHandler<RedirectDowngradeHandler>()
        // Inside the redirect handler on purpose: every hop it follows passes
        // through the guard, so a public feed cannot 302 us to 169.254.169.254.
        .AddHttpMessageHandler<OutboundUrlGuardHandler>()
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Resolves and filters at connect time, so a DNS answer that changes
            // between the guard's check and the socket open still cannot reach
            // a private address.
            ConnectCallback = GuardedConnect.ConnectAsync
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

// Cached-image restore from /data runs inside DatabaseBackupService.ExecuteAsync,
// after Kestrel is listening, so it no longer sits on the cold-start path.

// Writer: instantiate repos to create database tables in order.
// Reader: repos are still instantiated (singletons), but skip schema init
// because the DB is restored from Litestream with tables already in place.
var a = app.Services.GetRequiredService<IFeedRepository>();
var b = app.Services.GetRequiredService<IUserRepository>();
var c = app.Services.GetRequiredService<IItemRepository>();
var d = app.Services.GetRequiredService<ISystemStatsRepository>();

// Enable middleware 
app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseResponseCompression();
app.UseStaticFiles();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

// Setup endpoints
app.MapControllers();
app.MapGet("/api/healthz", () => Results.Ok(new { status = "healthy", role = config.IsReadOnly ? "reader" : "writer" })).AllowAnonymous();
app.MapAuthEndpoints(config.Auth);

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
