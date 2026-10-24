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

// The test-user mode authenticates every request as a fake admin. Finding it
// switched on in Production by accident must stop the app; a private single-user
// box opts in explicitly with AllowTestUserInProduction.
if (config.IsTestUserEnabled && builder.Environment.IsProduction() && !config.AllowTestUserInProduction)
{
    throw new InvalidOperationException(
        "RssAppConfig:IsTestUserEnabled is true in the Production environment. " +
        "That setting authenticates every request as a fake administrator; turn it off, " +
        "or set RssAppConfig:AllowTestUserInProduction=true for a private single-user box with no login.");
}
var dbConnections = new SqliteDbConnections(config.DbLocation, config.IsReadOnly);

// Writer durability: the live DB is seeded from the share copy and the backup
// service copies it back. The lock on the share is held for the whole process
// lifetime (taken here, released after the final backup on shutdown).
var backupPaths = new DatabaseBackupToFilePaths(
    ActiveDbPath: config.DbLocation,
    BackupDbPath: config.BackupDbPath ?? string.Empty,
    Interval: config.BackupInterval,
    LockStaleThreshold: config.BackupLockStaleAfter,
    ShutdownBackupTimeout: config.ShutdownBackupTimeout);
var startupLoggerFactory = LoggerFactory.Create(lb => lb.AddConsole().SetMinimumLevel(LogLevel.Information));
var writerLock = new ShareWriterLock(
    backupPaths.LockPath, backupPaths.LockStaleThreshold, backupPaths.HeartbeatInterval,
    startupLoggerFactory.CreateLogger<ShareWriterLock>());

// Under `litestream replicate` the entrypoint still seeds /tmp before exec, because
// Litestream must not see the file replaced underneath it (scaling mode, off).
var litestreamSupervised = string.Equals(
    Environment.GetEnvironmentVariable("RSSREADER_ENABLE_LITESTREAM_REPLICATION"), "true", StringComparison.OrdinalIgnoreCase);
if (!config.IsReadOnly && !string.IsNullOrEmpty(backupPaths.BackupDbPath) && !litestreamSupervised)
{
    // Before the repositories below open the live DB: answer the startup probe,
    // wait for the previous revision's final copy, take the lock, seed /tmp.
    try
    {
        await WriterStartupGate.RunAsync(
            args, backupPaths, writerLock,
            config.Auth.AllowedFrontendOrigins.Select(ReturnUrlValidator.NormalizeOrigin).ToArray(),
            startupLoggerFactory);
    }
    catch (OperationCanceledException)
    {
        return;
    }
}

// One-shot schema migration (DB-REDESIGN-SPEC.md section 7) before any
// repository opens the database. Runs inside one transaction and refuses to
// start on failure; the legacy tables stay as Legacy_* until dropped by hand.
MigrationState migrationState = new(DatabaseSchema.MigrationId, "skipped-reader", null, null, null);
if (!config.IsReadOnly)
{
    migrationState = LegacyMigration.RunIfNeeded(dbConnections, startupLoggerFactory.CreateLogger("LegacyMigration"));
}

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
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedFor;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add authn and authz: session cookie + one scheme per configured provider,
// plus the Static Web Apps header scheme during the migration window.
builder.Services.AddRssAuthentication(config);
builder.Services.AddRssRateLimiting(config.Auth);

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
        .AddSingleton(writerLock)
        .AddSingleton<DatabaseBackupToFileService>(sb =>
            new DatabaseBackupToFileService(
                sb.GetRequiredService<ILogger<DatabaseBackupToFileService>>(),
                backupPaths,
                writerLock))
        .AddHostedService(p => p.GetRequiredService<DatabaseBackupToFileService>())
        .AddHostedService<BackgroundWorker>()
        .AddSingleton<RefreshPacer>()
        .AddSingleton<IFeedRefresher, FeedRefresher>()
        .AddHostedService<FeedScheduler>()
        .AddHostedService<PostRetentionService>()
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

// Shutdown budget: Kestrel drains, the feed scheduler and worker stop, then the
// backup service's final copy runs (ShutdownBackupTimeout). The default 30 s is
// not enough for the copy; terminationGracePeriodSeconds in main.bicep is 120.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(110));

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
// Cross-site request forgery: the session cookie is SameSite=None (the
// frontend is another origin), so a state-changing request from a foreign
// origin is refused here by its Origin header. See CrossSiteRequestGuard.
app.UseMiddleware<CrossSiteRequestGuard>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Setup endpoints
app.MapControllers();

// One-box self-hosting: when the published frontend is mounted into wwwroot,
// serve it and let the Blazor router own every path the API does not.
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html")))
{
    app.MapFallbackToFile("index.html");
}
app.MapGet("/api/healthz", () => Results.Ok(new
{
    status = "healthy",
    role = config.IsReadOnly ? "reader" : "writer",
    migration = new
    {
        id = migrationState.Id,
        status = migrationState.Status,
        appliedUtc = migrationState.AppliedUtc,
        durationMs = migrationState.DurationMs,
        notes = migrationState.Notes,
    },
})).AllowAnonymous();
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
