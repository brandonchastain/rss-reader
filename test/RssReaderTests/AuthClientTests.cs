using RssApp.Config;
using WasmApp.Services;

namespace RssReaderTests;

[TestClass]
public sealed class AuthClientTests
{
    [TestMethod]
    public void ApiMode_UrlsHangOffApiBaseUrl()
    {
        var client = new AuthClient(new RssWasmConfig { ApiBaseUrl = "https://api.rss.example.com/" });

        Assert.AreEqual(
            "https://api.rss.example.com/auth/login/github?returnUrl=https%3A%2F%2Frss.example.com%2Ftimeline",
            client.LoginUrl("github", "https://rss.example.com/timeline"));
        Assert.AreEqual(
            "https://api.rss.example.com/auth/logout?returnUrl=https%3A%2F%2Frss.example.com%2F",
            client.LogoutUrl("https://rss.example.com/"));
    }

    [TestMethod]
    public async Task StaticWebAppsMode_UsesEasyAuthRoutes_OnTheSiteOrigin()
    {
        var client = new AuthClient(new RssWasmConfig { ApiBaseUrl = "https://rss.example.com/", UseStaticWebAppsAuth = true });

        Assert.AreEqual(
            "/.auth/login/aad?post_login_redirect_uri=%2Ftimeline%3Fa%3D1&prompt=select_account",
            client.LoginUrl("microsoft", "https://rss.example.com/timeline?a=1"),
            "SWA only accepts a relative post-login path");
        Assert.AreEqual("/.auth/logout?post_logout_redirect_uri=/signedout.html", client.LogoutUrl("https://rss.example.com/"));
        CollectionAssert.AreEqual(new[] { "microsoft" }, (await client.GetProvidersAsync()).ToArray());
    }
}
