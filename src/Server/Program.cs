using Microsoft.AspNetCore.Cors.Infrastructure;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Data;
using RssApp.RssClient;
using RssApp.Serialization;
using RssReader.Server.Services;

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
.AddHostedService<DatabaseBackupService>()
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

// Configure HTTPS for prod
if (!builder.Environment.IsDevelopment())
{
    // builder.Services.AddLettuceEncrypt();
    // builder.WebHost.UseKestrel(k =>
    // {
    //     var appServices = k.ApplicationServices;
    //     k.ConfigureHttpsDefaults(h =>
    //     {
    //         h.UseLettuceEncrypt(appServices);
    //     });
    // });
}

// Restore database from backup BEFORE building the app
RestoreDatabaseFromBackup();

var app = builder.Build();

// Instantiate repositories to ensure db tables are created in order.
var a = app.Services.GetRequiredService<IFeedRepository>();
var b = app.Services.GetRequiredService<IUserRepository>();
var c = app.Services.GetRequiredService<IItemRepository>();

static void RestoreDatabaseFromBackup()
{
    const string activeDbPath = "/tmp/storage.db";
    const string backupDbPath = "/data/storage.db";
    
    try
    {
        if (!File.Exists(backupDbPath))
        {
            Console.WriteLine($"No backup database found at {backupDbPath}. Starting with empty database");
            return;
        }

        if (File.Exists(activeDbPath))
        {
            Console.WriteLine($"Active database already exists at {activeDbPath}. Using existing.");
            return;
        }

        var activeDir = Path.GetDirectoryName(activeDbPath);
        if (!string.IsNullOrEmpty(activeDir) && !Directory.Exists(activeDir))
        {
            Directory.CreateDirectory(activeDir);
        }

        Console.WriteLine($"Restoring database from {backupDbPath} to {activeDbPath}");
        
        File.Copy(backupDbPath, activeDbPath, overwrite: true);
        
        if (File.Exists($"{backupDbPath}-wal"))
            File.Copy($"{backupDbPath}-wal", $"{activeDbPath}-wal", overwrite: true);
        if (File.Exists($"{backupDbPath}-shm"))
            File.Copy($"{backupDbPath}-shm", $"{activeDbPath}-shm", overwrite: true);
        
        var fileInfo = new FileInfo(activeDbPath);
        Console.WriteLine($"Database restored successfully. Size: {fileInfo.Length:N0} bytes");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to restore database from backup: {ex.Message}. Starting with empty database");
    }
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseCors("AllowSpecificOrigins");
// app.UseAuthorization();
app.MapControllers();
app.Run();