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

// Blob writer lease (BLOB-DURABILITY-SPEC.md 3.4), rollout step 2: taken beside the
// lock file and logged, nothing depends on it yet. Acquired while the gate below is
// answering the startup probe, because the old revision only releases it after it has
// been told to stop, and it is only told to stop once this revision's probe passes.
var processStartedUtc = DateTimeOffset.UtcNow;
var writerIdentity = $"{Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ?? "local"}/{Environment.MachineName}";
var writerState = new WriterState();
var writerLease = config.IsReadOnly
    ? null
    : BlobWriterLease.Create(config.Durability, startupLoggerFactory.CreateLogger<BlobWriterLease>(),
        config.Durability.RestoreEnabled ? config.Durability.HandoffLeasePollInterval : null);
using var leaseAcquireCts = new CancellationTokenSource();
var leaseAcquire = writerLease == null
    ? Task.FromResult(false)
    : Task.Run(() => writerLease.AcquireAsync(leaseAcquireCts.Token));

// Rollout step 3 (spec 3.2 steps 3 to 5), behind RssAppConfig:Durability:RestoreFromBlob:
// once the lease is held, restore /tmp from the Litestream replica instead of copying the
// share. The delegate returns false (after an error log) whenever the lease was not
// obtained or the restore fails, and the gate falls back to the share copy.
var durabilityLogger = startupLoggerFactory.CreateLogger("Durability");
Func<CancellationToken, Task<bool>>? restoreFromReplica = null;
Func<bool>? readyForTraffic = null;
if (config.Durability.RestoreFromBlob && !config.IsReadOnly && !litestreamSupervised)
{
    if (!config.Durability.RestoreEnabled || writerLease == null)
    {
        durabilityLogger.LogError(
            "Durability:RestoreFromBlob is on but needs LitestreamEnabled={Litestream} and the blob lease (configured={Lease}); seeding from the share instead.",
            config.Durability.LitestreamEnabled, writerLease != null);
    }
    else
    {
        // Cooperative handover first (spec 3.2 v2): follow the replica while the old
        // writer drains on request, so the lease wait is seconds rather than the
        // platform's SIGTERM delay. The gate's probe answers 503 until the follower's
        // initial restore is on disk (trafficReady), then 200.
        var trafficReady = new bool[1];
        readyForTraffic = () => Volatile.Read(ref trafficReady[0]);
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

        restoreFromReplica = async ct =>
        {
            if (await cooperative.RunAsync(ct)) return true;
            Volatile.Write(ref trafficReady[0], true);

            // Fallback 1: a fresh full restore once the lease is held (3.2 steps 3 to 5).
            // The caller falls back to the share copy when this returns false too.
#pragma warning disable VSTHRD003
            var leaseHeld = await leaseAcquire.WaitAsync(ct);
#pragma warning restore VSTHRD003
            if (!leaseHeld || !writerLease.IsHeld)
            {
                durabilityLogger.LogError("Startup: blob lease not held; not restoring from the replica, falling back to the share copy.");
                return false;
            }
            return await BlobRestoreSeeder.RestoreFromReplicaAsync(
                config.Durability, config.DbLocation, new LitestreamCli(config.Durability.LitestreamBinary),
                startupLoggerFactory.CreateLogger("BlobRestore"), ct);
        };
    }
}

if (!config.IsReadOnly && !string.IsNullOrEmpty(backupPaths.BackupDbPath) && !litestreamSupervised)
{
    // Before the repositories below open the live DB: answer the startup probe, then
    // either (restore on) wait for the lease and restore from the replica, or wait for
    // the previous revision's final copy, take the lock, and seed /tmp from the share.
    try
    {
        await WriterStartupGate.RunAsync(
            args, backupPaths, writerLock,
            config.Auth.AllowedFrontendOrigins.Select(ReturnUrlValidator.NormalizeOrigin).ToArray(),
            startupLoggerFactory,
            restoreFromReplica: restoreFromReplica,
            readyForTraffic: readyForTraffic);
    }
    catch (OperationCanceledException)
    {
        leaseAcquireCts.Cancel();
        if (writerLease != null) await writerLease.ReleaseAsync();
        return;
    }
}

var shareLockDeferred = false;
if (writerLease != null)
{
    // The gate returned with the share lock held (or given up on). The lease normally
    // arrives within seconds of it: the old writer releases its lock first, then its lease.
    // VSTHRD003 guards against JoinableTaskFactory deadlocks when awaiting a task
    // started elsewhere; ASP.NET has no synchronization context, so none is possible.
#pragma warning disable VSTHRD003
    try { await leaseAcquire; }
#pragma warning restore VSTHRD003
    catch (OperationCanceledException) { }
    shareLockDeferred = restoreFromReplica != null && !writerLock.IsHeld;
    durabilityLogger.LogInformation(
        "Startup: blob lease held={LeaseHeld}, share lock held={LockHeld}; {Verdict}.",
        writerLease.IsHeld, writerLock.IsHeld,
        shareLockDeferred ? "restored from the replica, share lock left to the backup service"
            : writerLease.IsHeld == writerLock.IsHeld ? "agree" : "DISAGREE");
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
        // Hosted services stop in reverse registration order, so the shutdown sequence of
        // BLOB-DURABILITY-SPEC.md 3.3 is this list read bottom-up: the retention service,
        // feed scheduler and worker stop first (nothing writes after this); Litestream runs
        // the final sync; the blob lease is released (the next writer may now restore from
        // the replica); the share copy runs and the lock file is released. The share copy
        // comes last because only a rollback to the previous image reads it.
        .AddSingleton(writerState)
        .AddSingleton<DatabaseBackupToFileService>(sb =>
            new DatabaseBackupToFileService(
                sb.GetRequiredService<ILogger<DatabaseBackupToFileService>>(),
                backupPaths,
                writerLock,
                writerState))
        .AddHostedService(p => p.GetRequiredService<DatabaseBackupToFileService>())
        .AddSingleton(sb => new WriterLeaseHostedService(
            writerLease, writerLock, sb.GetRequiredService<ILogger<WriterLeaseHostedService>>(), shareLockDeferred, writerState))
        .AddHostedService(p => p.GetRequiredService<WriterLeaseHostedService>())
        .AddSingleton(sb => new LitestreamSupervisor(
            config.Durability, config.DbLocation, sb.GetRequiredService<ILogger<LitestreamSupervisor>>()))
        .AddHostedService(p => p.GetRequiredService<LitestreamSupervisor>())
        .AddHostedService<BackgroundWorker>()
        .AddSingleton<RefreshPacer>()
        .AddSingleton<IFeedRefresher, FeedRefresher>()
        .AddHostedService<FeedScheduler>()
        .AddHostedService<PostRetentionService>()
        // Cooperative handover, the running writer's side (spec 3.2 v2): when the next
        // revision asks on the handoff blob, stop the writers above, checkpoint, final
        // sync, acknowledge, release the lease, then copy to the share for rollback.
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

if (config.Durability.RestoreEnabled && writerLease != null)
{
    // Cooperative handover, the running writer's side (spec 3.2 v2): when the next
    // revision asks on the handoff blob, stop the feed scheduler, worker and retention
    // service, checkpoint, final Litestream sync, acknowledge with the transaction id,
    // release the lease, then copy to the share for rollback. WriterHandoffService polls.
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
            AfterReleaseAsync = ct => sb.GetRequiredService<DatabaseBackupToFileService>().RunHandoverCopyAsync(ct),
        },
        writerIdentity, processStartedUtc, config.Durability.HandoffDrainTimeout,
        sb.GetRequiredService<ILogger<WriterHandover>>()));
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

// Mark the SIGTERM moment so the old revision's shutdown sequence (final DB backup,
// lock release, lease release) can be timed from the console logs.
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
        role = config.IsReadOnly ? "reader" : "writer",
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
