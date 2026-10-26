using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Memory;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Data;
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
var dbConnections = new SqliteDbConnections(config.DbLocation);

// Writer durability (BLOB-DURABILITY-SPEC.md): the live DB on local disk is restored from the
// Litestream replica at startup and replicated back by a Litestream child of this process.
var startupLoggerFactory = LoggerFactory.Create(lb => lb.AddConsole().SetMinimumLevel(LogLevel.Information));

// Blob writer lease (spec 3.4). Acquired while the gate below is answering the startup
// probe, because the old revision only releases it after it has been told to stop, and it
// is only told to stop once this revision's probe passes.
var processStartedUtc = DateTimeOffset.UtcNow;
var writerIdentity = $"{Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ?? "local"}/{Environment.MachineName}";
var writerState = new WriterState();
var writerLease = BlobWriterLease.Create(config.Durability, startupLoggerFactory.CreateLogger<BlobWriterLease>(),
    config.Durability.RestoreEnabled ? config.Durability.HandoffLeasePollInterval : null);
using var leaseAcquireCts = new CancellationTokenSource();
var leaseAcquire = writerLease == null
    ? Task.FromResult(false)
    : Task.Run(() => writerLease.AcquireAsync(leaseAcquireCts.Token));

// Spec 3.2 steps 3 to 5, behind RssAppConfig:Durability:RestoreFromBlob: once the lease is
// held, restore /tmp from the Litestream replica. Without it (local development, the e2e
// suite, a self-hosted box) there is no gate and SQLite creates the database on first use.
if (config.Durability.RestoreFromBlob)
{
    if (!config.Durability.RestoreEnabled || writerLease == null)
    {
        throw new InvalidOperationException(
            "RssAppConfig:Durability:RestoreFromBlob is on but needs LitestreamEnabled and the blob lease " +
            $"(LitestreamEnabled={config.Durability.LitestreamEnabled}, lease configured={writerLease != null}).");
    }

    // Cooperative handover first (spec 3.2 v2): follow the replica while the old writer
    // drains on request, so the lease wait is seconds rather than the platform's SIGTERM
    // delay. The gate's probe answers 503 until the follower's initial restore is on disk
    // (trafficReady), then 200.
    var trafficReady = new bool[1];
    var handoffChannel = new BlobWriterHandoffChannel(BlobWriterLease.CreateContainerClient(config.Durability), config.Durability.HandoffBlob);
    var cooperative = new CooperativeHandoverSeeder(
        config.Durability, config.DbLocation, handoffChannel,
        () => new LitestreamFollower(config.Durability, config.DbLocation, config.DbLocation + ".follow", startupLoggerFactory.CreateLogger("LitestreamFollower")),
        async ct =>
        {
#pragma warning disable VSTHRD003
            var held = await leaseAcquire.WaitAsync(ct);
#pragma warning restore VSTHRD003
            return held && writerLease.IsHeld;
        },
        () => Volatile.Write(ref trafficReady[0], true),
        writerIdentity,
        startupLoggerFactory.CreateLogger("Handover"));

    // Any failure returns false and startup throws: the container restarts and restores again.
    try
    {
        await WriterStartupGate.RunAsync(
            args,
            async ct =>
            {
                var restored = await cooperative.RunAsync(ct);
                Volatile.Write(ref trafficReady[0], true);
                return restored;
            },
            config.Auth.AllowedFrontendOrigins.Select(ReturnUrlValidator.NormalizeOrigin).ToArray(),
            startupLoggerFactory,
            readyForTraffic: () => Volatile.Read(ref trafficReady[0]));
    }
    catch (OperationCanceledException)
    {
        leaseAcquireCts.Cancel();
        await writerLease.ReleaseAsync();
        return;
    }
    catch
    {
        leaseAcquireCts.Cancel();
        await writerLease.ReleaseAsync();
        throw;
    }
}

// One-shot schema migration (DB-REDESIGN-SPEC.md section 7) before any
// repository opens the database. Runs inside one transaction and refuses to
// start on failure; the legacy tables stay as Legacy_* until dropped by hand.
var migrationState = LegacyMigration.RunIfNeeded(dbConnections, startupLoggerFactory.CreateLogger("LegacyMigration"));

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxConcurrentConnections = 500;
    serverOptions.Limits.MaxConcurrentUpgradedConnections = 500;
    serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
});

builder.Services.AddMemoryCache();
// Base IHttpClientFactory so services like FaviconService resolve; the named
// "RssClient" is configured below.
builder.Services.AddHttpClient();
// Outbound destination policy: the feed controller validates a new feed's URL
// before storing it.
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
    .AddSingleton<RepositoryFactory>(sb => new RepositoryFactory(dbConnections, sb, config.RebuildFtsOnStartup))
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
    .AddSingleton<FaviconService>()
    .AddSingleton<ThumbnailResolver>()
    .AddSingleton<ISystemStatsRepository>(sb =>
        new SQLiteSystemStatsRepository(dbConnections))
    .AddSingleton<SystemStatsCollector>()
    .AddSingleton<RssApp.Data.AdminDetailQueries>();

// Background feed refresh, RSS fetching, replication
builder.Services
    .AddSingleton<RssDeserializer>()
    .AddSingleton<BackgroundWorkQueue>()
    // Hosted services stop in reverse registration order, so the shutdown sequence of
    // BLOB-DURABILITY-SPEC.md 3.3 is this list read bottom-up: the retention service,
    // feed scheduler and worker stop first (nothing writes after this); Litestream runs
    // the final sync; the blob lease is released (the next writer may now restore from
    // the replica).
    .AddSingleton(writerState)
    .AddSingleton(sb => new WriterLeaseHostedService(
        writerLease, sb.GetRequiredService<ILogger<WriterLeaseHostedService>>(), writerState))
    .AddHostedService(p => p.GetRequiredService<WriterLeaseHostedService>())
    .AddSingleton(sb => new LitestreamSupervisor(
        config.Durability, config.DbLocation, sb.GetRequiredService<ILogger<LitestreamSupervisor>>()))
    .AddHostedService(p => p.GetRequiredService<LitestreamSupervisor>())
    .AddHostedService(sb => new ReplicaChainReporter(
        config.Durability, config.DbLocation, sb.GetRequiredService<ILogger<ReplicaChainReporter>>()))
    .AddHostedService<BackgroundWorker>()
    .AddSingleton<RefreshPacer>()
    .AddSingleton<IFeedRefresher, FeedRefresher>()
    .AddHostedService<FeedScheduler>()
    .AddHostedService<PostRetentionService>()
    // Cooperative handover, the running writer's side (spec 3.2 v2): when the next
    // revision asks on the handoff blob, stop the writers above, checkpoint, final
    // sync, acknowledge, release the lease.
    // Registered last so it is the first to stop and never races the shutdown order.
    .AddHostedService(sb => new WriterHandoffService(
        sb.GetService<WriterHandover>(), () => writerLease?.IsHeld == true, config.Durability.HandoffPollInterval,
        sb.GetRequiredService<ILogger<WriterHandoffService>>()))
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

// Shutdown budget: Kestrel drains, the feed scheduler and worker stop, then the final
// Litestream sync (FinalSyncTimeout 20 s, then the child's own 30 s safety net) and the
// lease release run. Must stay below terminationGracePeriodSeconds in main.bicep (60).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(50));

if (config.Durability.RestoreEnabled && writerLease != null)
{
    // Cooperative handover, the running writer's side (spec 3.2 v2): when the next
    // revision asks on the handoff blob, stop the feed scheduler, worker and retention
    // service, checkpoint, final Litestream sync, acknowledge with the transaction id,
    // release the lease. WriterHandoffService polls.
    builder.Services.AddSingleton(sb => new WriterHandover(
        writerState,
        new BlobWriterHandoffChannel(BlobWriterLease.CreateContainerClient(config.Durability), config.Durability.HandoffBlob),
        new WriterHandover.Steps
        {
            // Not IHostedService.StopAsync: the host treats a background service that ends
            // in OperationCanceledException outside shutdown as a failure and stops itself.
            // The three loops link WriterState.WritersStopping and exit on it; wait for
            // their ExecuteTask to settle.
            StopWritersAsync = async ct =>
            {
                writerState.RequestWritersStop();
                var tasks = sb.GetServices<IHostedService>()
                    .OfType<BackgroundService>()
                    .Where(s => s is FeedScheduler or BackgroundWorker or PostRetentionService)
                    .Select(s => s.ExecuteTask ?? Task.CompletedTask)
                    .Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default))
                    .ToArray();
                await Task.WhenAll(tasks).WaitAsync(ct);
            },
            CheckpointAsync = async ct =>
            {
                using var conn = dbConnections.OpenWrite();
                using var cmd = conn.CreateCommand();
                // Busy timeout 2 s: an in-flight read keeps TRUNCATE from completing and
                // the pragma then reports busy rather than failing; the final sync carries
                // whatever WAL is left, so this must not sit on the handover's critical path.
                cmd.CommandTimeout = 2;
                cmd.CommandText = "PRAGMA busy_timeout = 2000; PRAGMA wal_checkpoint(TRUNCATE);";
                await cmd.ExecuteNonQueryAsync(ct);
            },
            FinalSyncAsync = ct => sb.GetRequiredService<LitestreamSupervisor>().SyncAndStopAsync("Handoff", ct),
            ReleaseLeaseAsync = metadata => writerLease.ReleaseAsync(metadata),
        },
        writerIdentity, processStartedUtc, config.Durability.HandoffDrainTimeout,
        sb.GetRequiredService<ILogger<WriterHandover>>()));
}

builder.Services.AddControllers();

if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseKestrel();
}

var app = builder.Build();

// Mark the SIGTERM moment so the old revision's shutdown sequence (final sync,
// lease release) can be timed from the console logs.
{
    var shutdownLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RssReader.Server.Shutdown");
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var startedAt = DateTimeOffset.UtcNow;
    var shutdownSw = new System.Diagnostics.Stopwatch();
    lifetime.ApplicationStopping.Register(() =>
    {
        shutdownSw.Start();
        shutdownLogger.LogInformation(
            "Shutdown: ApplicationStopping (SIGTERM) at {At:O}; host uptime {Uptime}.",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - startedAt);
    });
    lifetime.ApplicationStopped.Register(() =>
        shutdownLogger.LogInformation(
            "Shutdown: ApplicationStopped at {At:O}; {Ms} ms after ApplicationStopping.",
            DateTimeOffset.UtcNow, shutdownSw.ElapsedMilliseconds));
}

// Instantiate repos to create database tables in order.
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
// After a cooperative handoff this revision no longer owns the database: mutating
// requests get 503 until the platform stops it; reads keep working (spec 3.2 v2).
app.Use(WriterState.Middleware(writerState));
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
app.MapGet("/api/healthz", async () =>
{
    // Row counts for the nightly restore drill (BLOB-DURABILITY-SPEC.md 9.5), which
    // compares them with the same counts in a restore of the blob replica. Cheap:
    // Posts and UserPostState have rowid tables; a count(*) on either is a btree walk
    // of a few milliseconds at the live size.
    DatabaseRowCounts counts;
    try
    {
        counts = await DatabaseRowCounts.ReadAsync(dbConnections);
    }
    catch (Exception ex)
    {
        counts = DatabaseRowCounts.Unavailable(ex.Message);
    }
    return Results.Ok(new
    {
        status = "healthy",
        role = "writer",
        handedOver = writerState.IsHandedOver ? new { to = writerState.HandedOverTo, atUtc = writerState.HandedOverAtUtc } : null,
        migration = new
        {
            id = migrationState.Id,
            status = migrationState.Status,
            appliedUtc = migrationState.AppliedUtc,
            durationMs = migrationState.DurationMs,
            notes = migrationState.Notes,
        },
        rows = counts,
    });
}).AllowAnonymous();
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
