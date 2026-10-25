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
}
