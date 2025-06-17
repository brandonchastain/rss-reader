using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RssApp.Components;
using RssApp.Components.Account;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Data;
using RssApp.RssClient;
using RssApp.Serialization;

var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += delegate {
    cancellationTokenSource.Cancel();
};

var config = RssAppConfig.LoadFromEnvironment();
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.ClearProviders();
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
    loggingBuilder.AddAzureWebAppDiagnostics();
});

builder.Services.AddMemoryCache();

builder.Services.AddHttpClient<FeedRefresher>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        UseDefaultCredentials = true
    });

builder.Services.AddSingleton<IUserRepository>(sb =>
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
    .AddSingleton<FeedRefresher>(sp =>
    {
        return new FeedRefresher(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<RssDeserializer>(),
            sp.GetRequiredService<ILogger<FeedRefresher>>(),
            sp.GetRequiredService<IFeedRepository>(),
            sp.GetRequiredService<IItemRepository>(),
            sp.GetRequiredService<IUserRepository>(),
            sp.GetRequiredService<BackgroundWorkQueue>(),
            config.CacheReloadInterval, 
            config.CacheReloadStartupDelay);
    })
    .AddTransient<IFeedClient, FeedClient>()
    .AddSingleton<OpmlSerializer>();

if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddLettuceEncrypt();
}

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityEmailSender>();


if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseKestrel(k =>
    {
        var appServices = k.ApplicationServices;
        k.ConfigureHttpsDefaults(h =>
        {
                h.UseLettuceEncrypt(appServices);
        });
    });
}
builder.Services.AddTransient<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender, EmailSender>();
builder.Services.Configure<AuthMessageSenderOptions>(builder.Configuration);

var app = builder.Build();

var a = app.Services.GetRequiredService<IFeedRepository>();
var b = app.Services.GetRequiredService<IUserRepository>();
var c = app.Services.GetRequiredService<IItemRepository>();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AllowAnonymous();

app.MapAdditionalIdentityEndpoints();

using (var scope = app.Services.CreateScope())
    using (var context = scope.ServiceProvider.GetService<ApplicationDbContext>())
        context.Database.Migrate();

app.Run();
