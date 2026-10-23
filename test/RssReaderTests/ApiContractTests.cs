#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RssApp.ComponentServices;
using RssApp.Contracts;
using RssApp.Data;
using RssApp.RssClient;

namespace RssReaderTests;

/// <summary>
/// Boots the real API in-process (WebApplicationFactory) against a temp SQLite
/// file, with every background service removed and the feed refresher and the
/// outbound URL guard replaced by offline stand-ins. Requests authenticate
/// through the Static Web Apps header path, so each test picks its user by
/// sending a different principal.
/// </summary>
internal sealed class ApiTestHost : IDisposable
{
    // Program reads its configuration (and the SWA handler reads the gateway
    // key) from process environment variables, so hosts are built one at a
    // time under this gate and every value is read before the gate is released.
    private static readonly object Gate = new();
    private const string GatewayKey = "contract-tests-gateway-key";

    private readonly string dbPath;
    private readonly WebApplicationFactory<Program> factory;

    /// <summary>
    /// First frontend origin the running host allows (the in-process host runs
    /// as Development, whose settings list localhost origins).
    /// </summary>
    public string AllowedOrigin =>
        Server.Authentication.ReturnUrlValidator.NormalizeOrigin(
            this.Services.GetRequiredService<RssApp.Config.RssAppConfig>().Auth.AllowedFrontendOrigins[0]);

    public ApiTestHost(bool isTestUserEnabled, string? environment = null, int? loginRateLimit = null, int? addFeedRateLimit = null)
    {
        this.dbPath = Path.Combine(Path.GetTempPath(), $"rss-contract-{Guid.NewGuid():N}.db");

        lock (Gate)
        {
            Environment.SetEnvironmentVariable("RSSREADER_API_KEY", GatewayKey);
            Environment.SetEnvironmentVariable("RssAppConfig__DbLocation", this.dbPath);
            Environment.SetEnvironmentVariable("RssAppConfig__IsTestUserEnabled", isTestUserEnabled ? "true" : "false");
            Environment.SetEnvironmentVariable("RssAppConfig__SchedulerEnabled", "false");
            Environment.SetEnvironmentVariable("RssAppConfig__IsReadOnly", "false");
            Environment.SetEnvironmentVariable("RssAppConfig__Auth__LoginRateLimitPerMinute", loginRateLimit?.ToString());
            Environment.SetEnvironmentVariable("RssAppConfig__Auth__AddFeedRateLimitPerMinute", addFeedRateLimit?.ToString());

            this.factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                if (environment != null) builder.UseEnvironment(environment);
                builder.ConfigureTestServices(services =>
                {
                    // No feed scheduler, worker, or backup copies: tests are offline and deterministic.
                    foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                    {
                        services.Remove(hosted);
                    }

                    services.RemoveAll<IFeedRefresher>();
                    services.AddSingleton<IFeedRefresher, NoOpFeedRefresher>();

                    // Resolve every host name to a public address without touching DNS.
                    services.RemoveAll<OutboundUrlGuard>();
                    services.AddSingleton(new OutboundUrlGuard((_, _) =>
                        Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })));
                });
            });

            // Builds the host now, while the environment still holds this host's values.
            using var warmup = this.factory.CreateClient();
        }
    }

    public IServiceProvider Services => this.factory.Services;

    /// <summary>Client that sends no credentials at all.</summary>
    public HttpClient Anonymous() => this.factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

    /// <summary>Client authenticated as the given provider user through the SWA header path.</summary>
    public HttpClient As(string userId, string displayName)
    {
        var client = this.Anonymous();
        var principal = JsonSerializer.Serialize(new
        {
            identityProvider = "github",
            userId,
            userDetails = displayName,
            userRoles = new[] { "authenticated" },
        });
        client.DefaultRequestHeaders.Add("X-Gateway-Key", GatewayKey);
        client.DefaultRequestHeaders.Add("X-User-Id", Convert.ToBase64String(Encoding.UTF8.GetBytes(principal)));
        return client;
    }

    public void Dispose()
    {
        this.factory.Dispose();
        try
        {
            SqliteConnection.ClearAllPools();
            File.Delete(this.dbPath);
        }
        catch
        {
            // Best effort; the file sits in the temp directory.
        }
    }
}

/// <summary>
/// Shape and behaviour assertions for the HTTP API as it is on main today.
/// The redesign of the posts storage promises that none of this changes.
/// </summary>
[TestClass]
public sealed class ApiContractTests
{
    private const string AliceFeed = "https://alpha.example.com/feed.xml";      // Alice only, tags tech
    private const string SharedFeed = "https://shared.example.com/rss";        // Alice (tags news, muted) and Bob (tag shared)
    private const string BobFeed = "https://beta.example.com/feed.xml";         // Bob only, tag beta
    private const string HiddenTag = "muted";
    private const int AliceFeedCount = 12;
    private const int SharedFeedCount = 15;
    private const int BobFeedCount = 10;

    private static ApiTestHost host = null!;
    private static HttpClient alice = null!;
    private static HttpClient bob = null!;
    private static int aliceId;
    private static int bobId;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly (string Name, JsonValueKind[] Kinds)[] ItemShape =
    {
        ("feedUrl", [JsonValueKind.String]),
        ("userId", [JsonValueKind.Number]),
        ("title", [JsonValueKind.String]),
        ("href", [JsonValueKind.String]),
        ("commentsHref", [JsonValueKind.String, JsonValueKind.Null]),
        ("publishDateOrder", [JsonValueKind.Number]),
        ("publishDate", [JsonValueKind.String]),
        ("id", [JsonValueKind.String]),
        ("content", [JsonValueKind.String, JsonValueKind.Null]),
        ("isRead", [JsonValueKind.True, JsonValueKind.False]),
        ("thumbnailUrl", [JsonValueKind.String, JsonValueKind.Null]),
        ("isSaved", [JsonValueKind.True, JsonValueKind.False]),
        ("isBeingPreviewed", [JsonValueKind.True, JsonValueKind.False]),
        ("feedTags", [JsonValueKind.Array]),
    };

    private static readonly (string Name, JsonValueKind[] Kinds)[] FeedShape =
    {
        ("feedId", [JsonValueKind.Number]),
        ("href", [JsonValueKind.String]),
        ("userId", [JsonValueKind.Number]),
        ("tags", [JsonValueKind.Array]),
        ("consecutiveFailures", [JsonValueKind.Number]),
        ("lastError", [JsonValueKind.String, JsonValueKind.Null]),
        ("lastFetchedUtc", [JsonValueKind.String, JsonValueKind.Null]),
        ("isFailing", [JsonValueKind.True, JsonValueKind.False]),
    };

    private static readonly (string Name, JsonValueKind[] Kinds)[] UserShape =
    {
        ("username", [JsonValueKind.String]),
        ("id", [JsonValueKind.Number]),
        ("aadUserId", [JsonValueKind.String, JsonValueKind.Null]),
        ("providerUserId", [JsonValueKind.String, JsonValueKind.Null]),
        ("isAdmin", [JsonValueKind.True, JsonValueKind.False]),
    };

    private static readonly (string Name, JsonValueKind[] Kinds)[] TagSettingShape =
    {
        ("tag", [JsonValueKind.String]),
        ("isHidden", [JsonValueKind.True, JsonValueKind.False]),
    };

    [ClassInitialize]
    public static async Task SeedAsync(TestContext _)
    {
        host = new ApiTestHost(isTestUserEnabled: false);
        alice = host.As("github:alice", "alice");
        bob = host.As("github:bob", "bob");

        aliceId = await RegisterAsync(alice);
        bobId = await RegisterAsync(bob);
        Assert.AreNotEqual(aliceId, bobId);

        await AddFeedAsync(alice, aliceId, AliceFeed);
        await AddFeedAsync(alice, aliceId, SharedFeed);
        await AddFeedAsync(bob, bobId, SharedFeed);
        await AddFeedAsync(bob, bobId, BobFeed);

        await TagAsync(alice, aliceId, AliceFeed, "tech");
        await TagAsync(alice, aliceId, SharedFeed, "news", HiddenTag);
        await TagAsync(bob, bobId, SharedFeed, "shared");
        await TagAsync(bob, bobId, BobFeed, "beta");

        var hide = await alice.PutAsJsonAsync("/api/feed/tagSettings", new TagSetting { Tag = HiddenTag, IsHidden = true });
        Assert.AreEqual(HttpStatusCode.OK, hide.StatusCode);

        // Posts are stored once per feed: the shared feed's posts are inserted a
        // single time and both subscribers see them through their subscription.
        var items = host.Services.GetRequiredService<IItemRepository>();
        var seeded = new List<NewsFeedItem>();
        seeded.AddRange(Posts(aliceId, AliceFeed, "alpha", AliceFeedCount, orderBase: 1_000_000));
        seeded.AddRange(Posts(aliceId, SharedFeed, "shared", SharedFeedCount, orderBase: 2_000_000));
        seeded.AddRange(Posts(bobId, BobFeed, "beta", BobFeedCount, orderBase: 3_000_000));
        await items.AddItemsAsync(seeded);

        // What matters is what each user can see, not how many rows were written.
        Assert.AreEqual(AliceFeedCount + SharedFeedCount,
            (await items.GetItemsAsync(new NewsFeed("%", aliceId), false, false, null, 0, 100)).Count());
        Assert.AreEqual(SharedFeedCount + BobFeedCount,
            (await items.GetItemsAsync(new NewsFeed("%", bobId), false, false, null, 0, 100)).Count());

        // Mixed read/saved state. Alice's state on alpha is what the exact filter
        // counts below rely on; nothing else in the class mutates alpha.
        var users = host.Services.GetRequiredService<IUserRepository>();
        var aliceUser = users.GetUserById(aliceId);
        var bobUser = users.GetUserById(bobId);
        var alpha = (await items.GetItemsAsync(new NewsFeed(AliceFeed, aliceId), false, false, null, 0, 100)).OrderBy(i => i.Href).ToList();
        foreach (var i in alpha.Take(4)) items.MarkAsRead(i, true, aliceUser);
        foreach (var i in alpha.Skip(4).Take(2)) items.SavePost(i, aliceUser);
        var aliceShared = (await items.GetItemsAsync(new NewsFeed(SharedFeed, aliceId), false, false, null, 0, 100)).OrderBy(i => i.Href).ToList();
        foreach (var i in aliceShared.Take(2)) items.MarkAsRead(i, true, aliceUser);
        var bobShared = (await items.GetItemsAsync(new NewsFeed(SharedFeed, bobId), false, false, null, 0, 100)).OrderBy(i => i.Href).ToList();
        foreach (var i in bobShared.Skip(3).Take(3)) items.MarkAsRead(i, true, bobUser);
        foreach (var i in bobShared.Skip(10).Take(1)) items.SavePost(i, bobUser);
        var beta = (await items.GetItemsAsync(new NewsFeed(BobFeed, bobId), false, false, null, 0, 100)).OrderBy(i => i.Href).ToList();
        foreach (var i in beta.Take(3)) items.MarkAsRead(i, true, bobUser);
        foreach (var i in beta.Skip(3).Take(2)) items.SavePost(i, bobUser);
    }

    [ClassCleanup]
    public static void TearDown()
    {
        alice?.Dispose();
        bob?.Dispose();
        host?.Dispose();
    }

    // ---------- seeding helpers ----------

    private static IEnumerable<NewsFeedItem> Posts(int userId, string feedUrl, string slug, int count, long orderBase)
    {
        for (var i = 0; i < count; i++)
        {
            var date = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(i);
            yield return new NewsFeedItem
            {
                FeedUrl = feedUrl,
                UserId = userId,
                Title = slug == "alpha" && i == 6 ? "Zebrafish genome notes" : $"{slug} post {i:D2}",
                Href = $"https://{slug}.example.com/posts/{i:D2}",
                CommentsHref = $"https://{slug}.example.com/posts/{i:D2}#comments",
                PublishDate = date.ToString("o"),
                PublishDateOrder = orderBase + i * 60,
                Content = $"<p>Body of {slug} {i}</p>",
            };
        }
    }

    private static async Task<int> RegisterAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/user/register", content: null);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        AssertShape(doc.RootElement, UserShape);
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    private static async Task AddFeedAsync(HttpClient client, int userId, string href)
    {
        var response = await client.PostAsJsonAsync("/api/feed", new NewsFeed(href, userId));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task TagAsync(HttpClient client, int userId, string href, params string[] tags)
    {
        var feed = new NewsFeed(href, userId);
        foreach (var t in tags) feed.Tags.Add(t);
        var response = await client.PostAsJsonAsync("/api/feed/tags", feed);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // ---------- assertion helpers ----------

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.StartsWith(response.Content.Headers.ContentType?.MediaType ?? "", "application/json", body);
        return JsonDocument.Parse(body);
    }

    private static void AssertShape(JsonElement element, (string Name, JsonValueKind[] Kinds)[] shape)
    {
        Assert.AreEqual(JsonValueKind.Object, element.ValueKind, element.ToString());
        foreach (var (name, kinds) in shape)
        {
            Assert.IsTrue(element.TryGetProperty(name, out var value), $"missing property '{name}' in {element}");
            CollectionAssert.Contains(kinds, value.ValueKind, $"property '{name}' has kind {value.ValueKind}");
        }
    }

    private static void AssertItemArray(JsonElement array, int? expectedCount = null)
    {
        Assert.AreEqual(JsonValueKind.Array, array.ValueKind);
        if (expectedCount.HasValue) Assert.AreEqual(expectedCount.Value, array.GetArrayLength(), array.ToString());
        foreach (var item in array.EnumerateArray()) AssertShape(item, ItemShape);
    }

    private static async Task<List<NewsFeedItem>> GetItemsAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var doc = await ReadJsonAsync(response);
        AssertItemArray(doc.RootElement);
        return doc.RootElement.Deserialize<List<NewsFeedItem>>(Json)!;
    }

    private static string Feed(string href) => Uri.EscapeDataString(href);

    // ---------- /api/healthz and /auth/me ----------

    [TestMethod]
    public async Task Healthz_IsAnonymous_AndReportsWriter()
    {
        using var client = host.Anonymous();
        var response = await client.GetAsync("/api/healthz");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.AreEqual("healthy", doc.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("writer", doc.RootElement.GetProperty("role").GetString());
    }

    [TestMethod]
    public async Task AuthMe_Authenticated_ReturnsClientPrincipalEnvelope()
    {
        var response = await alice.GetAsync("/auth/me");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var principal = doc.RootElement.GetProperty("clientPrincipal");
        AssertShape(principal, new (string, JsonValueKind[])[]
        {
            ("identityProvider", [JsonValueKind.String]),
            ("userId", [JsonValueKind.String]),
            ("userDetails", [JsonValueKind.String]),
            ("userRoles", [JsonValueKind.Array]),
        });
        Assert.AreEqual("github:alice", principal.GetProperty("userId").GetString());
        CollectionAssert.Contains(principal.GetProperty("userRoles").EnumerateArray().Select(r => r.GetString()).ToList(), "authenticated");
    }

    [TestMethod]
    public async Task AuthMe_Anonymous_ReturnsNullPrincipal()
    {
        using var client = host.Anonymous();
        var response = await client.GetAsync("/auth/me");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("clientPrincipal").ValueKind);
    }

    [TestMethod]
    public async Task ProtectedEndpoints_RejectAnonymousWith401()
    {
        using var client = host.Anonymous();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/item/timeline")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/feed")).StatusCode);
    }

    // ---------- /api/user ----------

    [TestMethod]
    public async Task User_ReturnsTheCallersRow()
    {
        var response = await alice.GetAsync("/api/user");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        AssertShape(doc.RootElement, UserShape);
        Assert.AreEqual(aliceId, doc.RootElement.GetProperty("id").GetInt32());
    }

    // ---------- timeline ----------

    [TestMethod]
    public async Task Timeline_FirstPage_IsNewestFirstWithItemShape()
    {
        var response = await alice.GetAsync("/api/item/timeline?pageSize=5");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        AssertItemArray(doc.RootElement, expectedCount: 5);

        var orders = doc.RootElement.EnumerateArray().Select(i => i.GetProperty("publishDateOrder").GetInt64()).ToList();
        CollectionAssert.AreEqual(orders.OrderByDescending(o => o).ToList(), orders, "timeline is newest first");
        Assert.AreEqual(orders.Count, orders.Distinct().Count(), "seeded PublishDateOrder values are distinct");
    }

    [TestMethod]
    public async Task Timeline_CursorPage_ContinuesAfterTheCursor()
    {
        var first = await GetItemsAsync(alice, "/api/item/timeline?pageSize=5");
        var last = first[^1];

        var next = await GetItemsAsync(alice, $"/api/item/timeline?pageSize=5&cursorPublishDateOrder={last.PublishDateOrder}&cursorId={last.Id}");
        Assert.AreEqual(5, next.Count);
        Assert.IsTrue(next.All(i => i.PublishDateOrder < last.PublishDateOrder), "every item on the cursor page is older than the cursor");
        Assert.IsFalse(next.Select(i => i.Href).Intersect(first.Select(i => i.Href)).Any());
    }

    [TestMethod]
    public async Task Timeline_CursorPaging_NeverRepeatsOrSkipsAPost()
    {
        var all = await GetItemsAsync(alice, "/api/item/timeline?pageSize=100");
        Assert.AreEqual(AliceFeedCount, all.Count, "alpha only: the muted shared feed is hidden for Alice");

        var seen = new List<string>();
        List<NewsFeedItem> page = await GetItemsAsync(alice, "/api/item/timeline?pageSize=5");
        var guard = 0;
        while (page.Count > 0 && guard++ < 20)
        {
            seen.AddRange(page.Select(i => i.Href));
            var last = page[^1];
            page = await GetItemsAsync(alice, $"/api/item/timeline?pageSize=5&cursorPublishDateOrder={last.PublishDateOrder}&cursorId={last.Id}");
        }

        Assert.AreEqual(seen.Count, seen.Distinct().Count(), "no post repeated across pages");
        CollectionAssert.AreEquivalent(all.Select(i => i.Href).ToList(), seen, "no post skipped across pages");
    }

    [TestMethod]
    public async Task Timeline_UnreadFilter_ReturnsOnlyUnread()
    {
        var items = await GetItemsAsync(alice, "/api/item/timeline?isFilterUnread=true&pageSize=100");
        Assert.AreEqual(AliceFeedCount - 4, items.Count);
        Assert.IsTrue(items.All(i => !i.IsRead));
    }

    [TestMethod]
    public async Task Timeline_SavedFilter_ReturnsOnlySaved()
    {
        var items = await GetItemsAsync(alice, "/api/item/timeline?isFilterSaved=true&pageSize=100");
        Assert.AreEqual(2, items.Count);
        Assert.IsTrue(items.All(i => i.IsSaved));
    }

    [TestMethod]
    public async Task Timeline_TagFilter_ReturnsOnlyThatTag()
    {
        var items = await GetItemsAsync(alice, "/api/item/timeline?filterTag=tech&pageSize=100");
        Assert.AreEqual(AliceFeedCount, items.Count);
        Assert.IsTrue(items.All(i => i.FeedTags.Contains("tech")));
        Assert.IsTrue(items.All(i => i.FeedUrl == AliceFeed));
    }

    [TestMethod]
    public async Task Timeline_HiddenTag_RemovesThatFeedForThatUserOnly()
    {
        var aliceItems = await GetItemsAsync(alice, "/api/item/timeline?pageSize=100");
        Assert.IsFalse(aliceItems.Any(i => i.FeedUrl == SharedFeed), "Alice muted the shared feed's tag");
        Assert.IsTrue(aliceItems.Any(i => i.FeedUrl == AliceFeed));

        var bobItems = await GetItemsAsync(bob, "/api/item/timeline?pageSize=100");
        Assert.AreEqual(SharedFeedCount, bobItems.Count(i => i.FeedUrl == SharedFeed), "Bob's view of the same feed is untouched");
    }

    // ---------- per-feed items ----------

    [TestMethod]
    public async Task FeedItems_ReturnsOnlyThatFeedsPosts()
    {
        var items = await GetItemsAsync(alice, $"/api/item/feed?href={Feed(AliceFeed)}&pageSize=100");
        Assert.AreEqual(AliceFeedCount, items.Count);
        Assert.IsTrue(items.All(i => i.FeedUrl == AliceFeed && i.UserId == aliceId));
    }

    [TestMethod]
    public async Task FeedItems_UnknownFeed_Is404()
    {
        var response = await alice.GetAsync($"/api/item/feed?href={Feed(BobFeed)}");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, "Bob's feed is not Alice's");
    }

    // ---------- read / saved state is per user ----------

    [TestMethod]
    public async Task MarkRead_IsPerUser()
    {
        var aliceView = await GetItemsAsync(alice, $"/api/item/feed?href={Feed(SharedFeed)}&pageSize=100");
        var target = aliceView.Single(i => i.Href.EndsWith("/07"));
        Assert.IsFalse(target.IsRead);

        var response = await alice.PostAsync($"/api/item/markAsRead?itemId={target.Id}&isRead=true", content: null);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        aliceView = await GetItemsAsync(alice, $"/api/item/feed?href={Feed(SharedFeed)}&pageSize=100");
        Assert.IsTrue(aliceView.Single(i => i.Href == target.Href).IsRead);

        var bobView = await GetItemsAsync(bob, $"/api/item/feed?href={Feed(SharedFeed)}&pageSize=100");
        Assert.IsFalse(bobView.Single(i => i.Href == target.Href).IsRead, "Alice reading a post does not read it for Bob");
    }

    [TestMethod]
    public async Task MarkRead_Post_AndUnread_RoundTrip()
    {
        var items = await GetItemsAsync(bob, $"/api/item/feed?href={Feed(BobFeed)}&pageSize=100");
        var target = items.Single(i => i.Href.EndsWith("/08"));

        var read = await bob.PostAsync($"/api/item/markAsRead?itemId={target.Id}&isRead=true", content: null);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.IsTrue((await GetItemsAsync(bob, $"/api/item/feed?href={Feed(BobFeed)}&pageSize=100")).Single(i => i.Href == target.Href).IsRead);

        var unread = await bob.PostAsync($"/api/item/markAsRead?itemId={target.Id}&isRead=false", content: null);
        Assert.AreEqual(HttpStatusCode.OK, unread.StatusCode);
        Assert.IsFalse((await GetItemsAsync(bob, $"/api/item/feed?href={Feed(BobFeed)}&pageSize=100")).Single(i => i.Href == target.Href).IsRead);
    }

    [TestMethod]
    public async Task MarkRead_UnknownItem_Is404()
    {
        var response = await bob.PostAsync("/api/item/markAsRead?itemId=999999&isRead=true", content: null);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task MarkRead_Get_IsNoLongerARoute()
    {
        // A GET that changed state could be fired cross-site by an <img> with
        // the session cookie attached; only POST remains.
        var response = await bob.GetAsync("/api/item/markAsRead?itemId=1&isRead=true");
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteFeed_UsesDelete_AndPostIsGone()
    {
        const string href = "https://epsilon.example.com/feed.xml";
        var add = await bob.PostAsJsonAsync("/api/feed", new NewsFeed(href, bobId));
        Assert.AreEqual(HttpStatusCode.Created, add.StatusCode);

        var post = await bob.PostAsync($"/api/feed/delete?href={Uri.EscapeDataString(href)}", content: null);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, post.StatusCode, "query-bound POST could be forged by a cross-site form");

        var delete = await bob.DeleteAsync($"/api/feed/delete?href={Uri.EscapeDataString(href)}");
        Assert.AreEqual(HttpStatusCode.OK, delete.StatusCode);

        var list = await bob.GetFromJsonAsync<List<NewsFeed>>("/api/feed", Json);
        Assert.IsFalse(list!.Any(f => f.Href == href));

        var again = await bob.DeleteAsync($"/api/feed/delete?href={Uri.EscapeDataString(href)}");
        Assert.AreEqual(HttpStatusCode.NotFound, again.StatusCode);
    }

    [TestMethod]
    public async Task Save_IsPerUser_AndUnsaveReverts()
    {
        var bobView = await GetItemsAsync(bob, $"/api/item/feed?href={Feed(SharedFeed)}&pageSize=100");
        var target = bobView.Single(i => i.Href.EndsWith("/08"));
        Assert.IsFalse(target.IsSaved);

        var save = await bob.PostAsJsonAsync("/api/item/save", target);
        Assert.AreEqual(HttpStatusCode.OK, save.StatusCode);
        Assert.IsTrue((await GetItemsAsync(bob, $"/api/item/feed?href={Feed(SharedFeed)}&pageSize=100")).Single(i => i.Href == target.Href).IsSaved);
        Assert.IsTrue((await GetItemsAsync(bob, "/api/item/timeline?isFilterSaved=true&pageSize=100")).Any(i => i.Href == target.Href));

        var aliceView = await GetItemsAsync(alice, $"/api/item/feed?href={Feed(SharedFeed)}&pageSize=100");
        Assert.IsFalse(aliceView.Single(i => i.Href == target.Href).IsSaved, "Bob saving a post does not save it for Alice");

        var unsave = await bob.PostAsJsonAsync("/api/item/unsave", target);
        Assert.AreEqual(HttpStatusCode.OK, unsave.StatusCode);
        Assert.IsFalse((await GetItemsAsync(bob, $"/api/item/feed?href={Feed(SharedFeed)}&pageSize=100")).Single(i => i.Href == target.Href).IsSaved);
    }

    [TestMethod]
    public async Task Save_ForAnotherUser_Is403()
    {
        var items = await GetItemsAsync(bob, $"/api/item/feed?href={Feed(BobFeed)}&pageSize=1");
        var response = await alice.PostAsJsonAsync("/api/item/save", items[0]);
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- search ----------

    [TestMethod]
    public async Task Search_FindsByTitle_PerUser()
    {
        var hits = await GetItemsAsync(alice, "/api/item/search?query=zebrafish");
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(AliceFeed, hits[0].FeedUrl);

        var bobHits = await GetItemsAsync(bob, "/api/item/search?query=zebrafish");
        Assert.AreEqual(0, bobHits.Count, "search is scoped to the caller's posts");
    }

    [TestMethod]
    public async Task Search_WithoutQuery_Is400()
    {
        var response = await alice.GetAsync("/api/item/search");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- new-item count ----------

    [TestMethod]
    public async Task NewCount_ReturnsNumberOfPostsNewerThanTheCursor()
    {
        var items = await GetItemsAsync(alice, "/api/item/timeline?pageSize=100");
        var cursor = items[2];

        var response = await alice.GetAsync($"/api/item/newCount?cursorPublishDateOrder={cursor.PublishDateOrder}&cursorId={cursor.Id}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(response.Headers.ETag, "newCount carries an ETag for revalidation");
        using var doc = await ReadJsonAsync(response);
        Assert.AreEqual(JsonValueKind.Number, doc.RootElement.ValueKind);
        Assert.AreEqual(2, doc.RootElement.GetInt32());
    }

    [TestMethod]
    public async Task NewCount_RespectsTheUnreadFilter()
    {
        var items = await GetItemsAsync(alice, "/api/item/timeline?pageSize=100");
        var oldest = items[^1];
        var response = await alice.GetAsync($"/api/item/newCount?isFilterUnread=true&cursorPublishDateOrder={oldest.PublishDateOrder}&cursorId={oldest.Id}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var unreadNewer = items.Count(i => !i.IsRead && i.PublishDateOrder > oldest.PublishDateOrder);
        Assert.AreEqual(unreadNewer, int.Parse(await response.Content.ReadAsStringAsync()));
    }

    // ---------- feeds ----------

    [TestMethod]
    public async Task FeedList_ReturnsTheCallersFeedsWithTags()
    {
        var response = await alice.GetAsync("/api/feed");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        foreach (var feed in doc.RootElement.EnumerateArray()) AssertShape(feed, FeedShape);

        var byHref = doc.RootElement.EnumerateArray().ToDictionary(f => f.GetProperty("href").GetString()!);
        Assert.IsTrue(byHref.ContainsKey(AliceFeed));
        Assert.IsTrue(byHref.ContainsKey(SharedFeed));
        Assert.IsFalse(byHref.ContainsKey(BobFeed));
        CollectionAssert.AreEquivalent(new[] { "news", HiddenTag }, byHref[SharedFeed].GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList());
        Assert.IsTrue(byHref.Values.All(f => f.GetProperty("userId").GetInt32() == aliceId));
    }

    [TestMethod]
    public async Task AddFeed_Returns201WithTheFeed_AndItAppearsInTheList()
    {
        const string href = "https://gamma.example.com/feed.xml";
        var response = await bob.PostAsJsonAsync("/api/feed", new NewsFeed(href, bobId));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var doc = await ReadJsonAsync(response);
        AssertShape(doc.RootElement, FeedShape);
        Assert.AreEqual(href, doc.RootElement.GetProperty("href").GetString());

        var list = await bob.GetFromJsonAsync<List<NewsFeed>>("/api/feed", Json);
        Assert.IsTrue(list!.Any(f => f.Href == href));
    }

    [TestMethod]
    public async Task AddFeed_ForAnotherUser_Is403()
    {
        var response = await bob.PostAsJsonAsync("/api/feed", new NewsFeed("https://delta.example.com/feed.xml", aliceId));
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task AddFeed_WithoutUrl_Is400()
    {
        var response = await bob.PostAsJsonAsync("/api/feed", new NewsFeed("", bobId));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task AddFeed_AlreadySubscribedByAnotherUser_SeesExistingPostsImmediately()
    {
        var response = await bob.PostAsJsonAsync("/api/feed", new NewsFeed(AliceFeed, bobId));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);

        var items = await GetItemsAsync(bob, $"/api/item/feed?href={Feed(AliceFeed)}&pageSize=100");
        Assert.AreEqual(AliceFeedCount, items.Count, "Bob sees the posts Alice's subscription already fetched");
        Assert.IsTrue(items.All(i => !i.IsRead), "but with his own, fresh read state");
    }

    // ---------- tags ----------

    [TestMethod]
    public async Task TagUpdate_AddsTagsToTheFeedAndItsPosts()
    {
        // A user of its own: the feed list is cached per user, and a concurrent
        // read of Bob's list from another test could race the tag write.
        const string href = "https://tagged.example.com/feed.xml";
        using var dave = host.As("github:dave", "dave");
        var daveId = await RegisterAsync(dave);
        await AddFeedAsync(dave, daveId, href);
        await host.Services.GetRequiredService<IItemRepository>().AddItemsAsync(Posts(daveId, href, "tagged", 4, orderBase: 4_000_000));

        var feed = new NewsFeed(href, daveId);
        feed.Tags.Add("Extra");
        var response = await dave.PostAsJsonAsync("/api/feed/tags", feed);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var list = await dave.GetFromJsonAsync<List<NewsFeed>>("/api/feed", Json);
        CollectionAssert.Contains(list!.Single(f => f.Href == href).Tags.ToList(), "extra", "tags are stored lower-cased");

        var items = await GetItemsAsync(dave, $"/api/item/feed?href={Feed(href)}&pageSize=100");
        Assert.AreEqual(4, items.Count);
        Assert.IsTrue(items.All(i => i.FeedTags.Contains("extra")), "existing posts carry the new tag");

        var tags = await dave.GetAsync($"/api/feed/tags?userId={daveId}");
        Assert.AreEqual(HttpStatusCode.OK, tags.StatusCode);
        using var doc = await ReadJsonAsync(tags);
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        CollectionAssert.Contains(doc.RootElement.EnumerateArray().Select(t => t.GetString()).ToList(), "extra");
    }

    [TestMethod]
    public async Task TagSettings_ListAndUpdate_ReturnTagSettingShape()
    {
        var get = await alice.GetAsync("/api/feed/tagSettings");
        Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
        using (var doc = await ReadJsonAsync(get))
        {
            Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
            foreach (var s in doc.RootElement.EnumerateArray()) AssertShape(s, TagSettingShape);
            var muted = doc.RootElement.EnumerateArray().Single(s => s.GetProperty("tag").GetString() == HiddenTag);
            Assert.IsTrue(muted.GetProperty("isHidden").GetBoolean());
            Assert.IsFalse(doc.RootElement.EnumerateArray().Single(s => s.GetProperty("tag").GetString() == "tech").GetProperty("isHidden").GetBoolean());
        }

        // A tag Bob has on no feed: the setting round-trips without changing any timeline.
        var put = await bob.PutAsJsonAsync("/api/feed/tagSettings", new TagSetting { Tag = "unused", IsHidden = true });
        Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);
        using (var doc = await ReadJsonAsync(put))
        {
            Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
            foreach (var s in doc.RootElement.EnumerateArray()) AssertShape(s, TagSettingShape);
        }
    }

    // ---------- OPML ----------

    [TestMethod]
    public async Task OpmlExport_ThenImport_RoundTripsFeedsAndTags()
    {
        var export = await alice.GetAsync($"/api/feed/exportOpml?userId={aliceId}");
        Assert.AreEqual(HttpStatusCode.OK, export.StatusCode);
        var opml = await export.Content.ReadAsStringAsync();
        StringAssert.Contains(opml, "<opml");
        StringAssert.Contains(opml, $"xmlUrl=\"{AliceFeed}\"");
        StringAssert.Contains(opml, $"xmlUrl=\"{SharedFeed}\"");
        StringAssert.Contains(opml, "category=\"");

        using var carol = host.As("github:carol", "carol");
        var carolId = await RegisterAsync(carol);
        var import = await carol.PostAsJsonAsync("/api/feed/importOpml", new OpmlImport { OpmlContent = opml, UserId = carolId });
        Assert.AreEqual(HttpStatusCode.OK, import.StatusCode, await import.Content.ReadAsStringAsync());

        var aliceFeeds = await alice.GetFromJsonAsync<List<NewsFeed>>("/api/feed", Json);
        var carolFeeds = await carol.GetFromJsonAsync<List<NewsFeed>>("/api/feed", Json);
        CollectionAssert.AreEquivalent(aliceFeeds!.Select(f => f.Href).ToList(), carolFeeds!.Select(f => f.Href).ToList());
        foreach (var feed in aliceFeeds)
        {
            var imported = carolFeeds.Single(f => f.Href == feed.Href);
            Assert.AreEqual(carolId, imported.UserId);
            CollectionAssert.AreEquivalent(feed.Tags.ToList(), imported.Tags.ToList(), $"tags of {feed.Href}");
        }
    }

    [TestMethod]
    public async Task OpmlExport_ForAnotherUser_Is403()
    {
        var response = await bob.GetAsync($"/api/feed/exportOpml?userId={aliceId}");
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- delete-all ----------

    [TestMethod]
    public async Task DeleteAll_OutsideTestUserMode_Is403()
    {
        var response = await bob.DeleteAsync("/api/item/all");
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(BobFeedCount, (await GetItemsAsync(bob, $"/api/item/feed?href={Feed(BobFeed)}&pageSize=100")).Count, "nothing was deleted");
    }
}

/// <summary>
/// The delete-all endpoint only works with the test user enabled, which pins
/// every request to one fixed identity, so it gets a host of its own.
/// </summary>
[TestClass]
public sealed class ApiContractTestUserModeTests
{
    private static ApiTestHost host = null!;

    [ClassInitialize]
    public static void Boot(TestContext _) => host = new ApiTestHost(isTestUserEnabled: true);

    [ClassCleanup]
    public static void TearDown() => host?.Dispose();

    [TestMethod]
    public async Task DeleteAll_RemovesEveryPostOfTheTestUser()
    {
        using var client = host.Anonymous();
        var register = await client.PostAsync("/api/user/register", content: null);
        Assert.AreEqual(HttpStatusCode.Created, register.StatusCode);
        var user = (await register.Content.ReadFromJsonAsync<RssUser>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

        const string href = "https://solo.example.com/feed.xml";
        var add = await client.PostAsJsonAsync("/api/feed", new NewsFeed(href, user.Id));
        Assert.AreEqual(HttpStatusCode.Created, add.StatusCode);

        var items = host.Services.GetRequiredService<IItemRepository>();
        await items.AddItemsAsync(Enumerable.Range(0, 5).Select(i => new NewsFeedItem
        {
            FeedUrl = href,
            UserId = user.Id,
            Title = $"solo {i}",
            Href = $"https://solo.example.com/{i}",
            PublishDate = "2025-01-01T00:00:00Z",
            PublishDateOrder = 100 + i,
            Content = "<p>x</p>",
        }));
        Assert.AreEqual(5, (await client.GetFromJsonAsync<List<NewsFeedItem>>("/api/item/timeline?pageSize=100"))!.Count);

        var delete = await client.DeleteAsync("/api/item/all");
        Assert.AreEqual(HttpStatusCode.OK, delete.StatusCode);

        var after = await client.GetAsync("/api/item/timeline?pageSize=100");
        Assert.AreEqual(HttpStatusCode.OK, after.StatusCode);
        using var doc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.AreEqual(0, doc.RootElement.GetArrayLength());
    }
}
