#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RssApp.Contracts;
using RssApp.Data;
using Server.Authentication;

namespace RssReaderTests;

/// <summary>
/// Security properties of the HTTP surface that must hold regardless of how
/// the storage or the controllers evolve: cross-site writes are refused on
/// every mutating route, the test-user bypass cannot start in Production, and
/// the cheap-to-hit endpoints are rate limited.
/// </summary>
[TestClass]
public sealed class SecurityContractTests
{
    private const string EvilOrigin = "https://evil.example";

    private static ApiTestHost host = null!;
    private static HttpClient user = null!;
    private static int userId;

    [ClassInitialize]
    public static async Task SeedAsync(TestContext _)
    {
        host = new ApiTestHost(isTestUserEnabled: false, loginRateLimit: 5, addFeedRateLimit: 4);
        user = host.As("github:carol", "carol");
        var register = await user.PostAsync("/api/user/register", content: null);
        Assert.AreEqual(HttpStatusCode.Created, register.StatusCode, await register.Content.ReadAsStringAsync());
        var me = await user.GetFromJsonAsync<RssUser>("/api/user");
        userId = me!.Id;
    }

    [ClassCleanup]
    public static void Teardown() => host.Dispose();

    // ---------- CSRF: every mutating route ----------

    /// <summary>
    /// Every state-changing route the API exposes. Keep in step with the
    /// controllers: a new mutating route must be added here, which is the
    /// point of the test.
    /// </summary>
    private static IEnumerable<object[]> MutatingRoutes()
    {
        yield return new object[] { "POST", "/api/user/register", "" };
        yield return new object[] { "POST", "/api/feed", "{\"href\":\"https://csrf.example.com/feed.xml\",\"userId\":0}" };
        yield return new object[] { "POST", "/api/feed/importOpml", "{\"opmlContent\":\"<opml/>\",\"userId\":0}" };
        yield return new object[] { "POST", "/api/feed/tags", "{\"href\":\"https://csrf.example.com/feed.xml\",\"userId\":0,\"tags\":[\"x\"]}" };
        yield return new object[] { "PUT", "/api/feed/tagSettings", "{\"tag\":\"x\",\"isHidden\":true}" };
        yield return new object[] { "DELETE", "/api/feed/delete?href=https%3A%2F%2Fcsrf.example.com%2Ffeed.xml", "" };
        yield return new object[] { "POST", "/api/item/markAsRead?itemId=1&isRead=true", "" };
        yield return new object[] { "POST", "/api/item/save", "{\"id\":\"1\",\"href\":\"x\",\"feedUrl\":\"x\",\"userId\":0}" };
        yield return new object[] { "POST", "/api/item/unsave", "{\"id\":\"1\",\"href\":\"x\",\"feedUrl\":\"x\",\"userId\":0}" };
        yield return new object[] { "DELETE", "/api/item/all", "" };
        yield return new object[] { "DELETE", "/api/user", "" };
    }

    [DataTestMethod]
    [DynamicData(nameof(MutatingRoutes), DynamicDataSourceType.Method)]
    public async Task MutatingRoute_FromAForeignOrigin_IsRefusedBeforeAuth(string method, string path, string body)
    {
        using var request = Build(method, path, body);
        request.Headers.TryAddWithoutValidation("Origin", EvilOrigin);
        var response = await user.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{method} {path} accepted a cross-site request");
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Cross-site");
    }

    [DataTestMethod]
    [DynamicData(nameof(MutatingRoutes), DynamicDataSourceType.Method)]
    public async Task MutatingRoute_FromANullOrigin_IsRefused(string method, string path, string body)
    {
        // Sandboxed iframes and some redirects send the literal "null".
        using var request = Build(method, path, body);
        request.Headers.TryAddWithoutValidation("Origin", "null");
        var response = await user.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{method} {path}");
    }

    [DataTestMethod]
    [DynamicData(nameof(MutatingRoutes), DynamicDataSourceType.Method)]
    public async Task MutatingRoute_FromTheFrontendOrigin_IsNotRefusedByTheGuard(string method, string path, string body)
    {
        // Destructive routes are exercised as an anonymous caller: the guard
        // runs before authentication, so a 401 proves it let the request
        // through, without actually deleting anything.
        var destructive = path.StartsWith("/api/item/all") || path == "/api/user";
        var client = destructive ? host.Anonymous() : user;
        using var request = Build(method, path, body);
        request.Headers.TryAddWithoutValidation("Origin", host.AllowedOrigin);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        // Controllers may still answer 403 for their own reasons (acting on
        // another user's row); what must not happen is the guard's refusal.
        Assert.IsFalse(text.Contains("Cross-site"), $"{method} {path}: refused by the guard ({(int)response.StatusCode} {text})");
    }

    [TestMethod]
    public async Task SafeMethods_AreNeverRefusedByOrigin()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/feed");
        request.Headers.TryAddWithoutValidation("Origin", EvilOrigin);
        var response = await user.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "reads are protected by CORS on the response side, not refused");
    }

    [TestMethod]
    public void Guard_Decision_Table()
    {
        var allowed = new HashSet<string>(new[] { "https://rss.example.com" }, StringComparer.OrdinalIgnoreCase);
        Assert.IsFalse(CrossSiteRequestGuard.IsForbidden("GET", "https://evil.example", allowed));
        Assert.IsFalse(CrossSiteRequestGuard.IsForbidden("POST", null, allowed), "no Origin = not a browser cross-site request");
        Assert.IsFalse(CrossSiteRequestGuard.IsForbidden("POST", "https://rss.example.com", allowed));
        Assert.IsFalse(CrossSiteRequestGuard.IsForbidden("POST", "https://RSS.example.com/", allowed));
        Assert.IsTrue(CrossSiteRequestGuard.IsForbidden("POST", "https://evil.example", allowed));
        Assert.IsTrue(CrossSiteRequestGuard.IsForbidden("POST", "null", allowed));
        Assert.IsTrue(CrossSiteRequestGuard.IsForbidden("DELETE", "https://rss.example.com.evil.example", allowed));
        Assert.IsTrue(CrossSiteRequestGuard.IsForbidden("PUT", "http://rss.example.com", allowed), "scheme is part of the origin");
    }

    // ---------- test-user bypass cannot run in Production ----------

    [TestMethod]
    public void TestUserMode_InProduction_RefusesToStart()
    {
        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var production = new ApiTestHost(isTestUserEnabled: true, environment: "Production");
        });
        StringAssert.Contains(ex.Message, "IsTestUserEnabled");
    }

    [TestMethod]
    public async Task TestUserMode_InDevelopment_StillWorksForLocalRuns()
    {
        using var dev = new ApiTestHost(isTestUserEnabled: true, environment: "Development");
        var response = await dev.Anonymous().GetAsync("/auth/me");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "test-user-id");
    }

    [TestMethod]
    public async Task Production_WithoutTestUser_AnonymousIsAnonymous()
    {
        using var production = new ApiTestHost(isTestUserEnabled: false, environment: "Production");
        var response = await production.Anonymous().GetAsync("/api/feed");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- rate limits ----------

    [TestMethod]
    public async Task Login_IsRateLimitedPerClient()
    {
        // Provider "nobody" is not configured, so each call is a cheap 404
        // until the limiter (5/min for this host) answers 429 instead.
        var anonymous = host.Anonymous();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++)
        {
            var response = await anonymous.GetAsync("/auth/login/nobody");
            statuses.Add(response.StatusCode);
        }
        Assert.AreEqual(5, statuses.Count(s => s == HttpStatusCode.NotFound), string.Join(",", statuses));
        Assert.AreEqual(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests), string.Join(",", statuses));
    }

    [TestMethod]
    public async Task AddFeed_IsRateLimitedPerUser()
    {
        // A fresh user, so permits used by the other tests in this class do not count.
        var dave = host.As("github:dave", "dave");
        var register = await dave.PostAsync("/api/user/register", content: null);
        var daveId = (await register.Content.ReadFromJsonAsync<RssUser>())!.Id;
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var response = await dave.PostAsJsonAsync("/api/feed", new NewsFeed($"https://limit{i}.example.com/feed.xml", daveId));
            statuses.Add(response.StatusCode);
        }
        Assert.AreEqual(4, statuses.Count(s => s == HttpStatusCode.Created), string.Join(",", statuses));
        Assert.AreEqual(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests), string.Join(",", statuses));

        // Reads are not limited.
        var list = await dave.GetAsync("/api/feed");
        Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);
    }

    private static HttpRequestMessage Build(string method, string path, string body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (!string.IsNullOrEmpty(body))
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }
}
