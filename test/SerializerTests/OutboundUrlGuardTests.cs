namespace SerializerTests;
using Microsoft.Extensions.DependencyInjection;
using RssApp.RssClient;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

[TestClass]
public sealed class OutboundUrlGuardTests
{
    private static readonly IPAddress Public = IPAddress.Parse("93.184.216.34");

    /// <summary>Offline resolver: a fixed answer per host name, SocketException for unknown names.</summary>
    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolver(params (string host, string ip)[] table)
        => (host, _) =>
        {
            foreach (var (h, ip) in table)
            {
                if (string.Equals(h, host, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new[] { IPAddress.Parse(ip) });
                }
            }
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        };

    private static Func<string, CancellationToken, Task<IPAddress[]>> AlwaysPublic
        => (_, _) => Task.FromResult(new[] { Public });

    [DataTestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("127.255.255.254")]
    [DataRow("0.0.0.0")]
    [DataRow("10.0.0.1")]
    [DataRow("10.255.255.255")]
    [DataRow("100.64.0.1")]
    [DataRow("100.127.255.255")]
    [DataRow("169.254.169.254")]   // Azure/AWS/GCP instance metadata
    [DataRow("169.254.0.1")]
    [DataRow("172.16.0.1")]
    [DataRow("172.31.255.255")]
    [DataRow("192.0.0.1")]
    [DataRow("192.0.2.1")]
    [DataRow("192.168.1.1")]
    [DataRow("198.18.0.1")]
    [DataRow("198.19.255.255")]
    [DataRow("198.51.100.1")]
    [DataRow("203.0.113.1")]
    [DataRow("224.0.0.1")]
    [DataRow("239.255.255.255")]
    [DataRow("240.0.0.1")]
    [DataRow("255.255.255.255")]
    [DataRow("::1")]
    [DataRow("::")]
    [DataRow("fe80::1")]
    [DataRow("fc00::1")]
    [DataRow("fd12:3456::1")]
    [DataRow("ff02::1")]
    [DataRow("::ffff:127.0.0.1")]   // IPv4-mapped loopback
    [DataRow("::ffff:10.0.0.1")]    // IPv4-mapped private
    [DataRow("64:ff9b::7f00:1")]    // NAT64 of 127.0.0.1
    [DataRow("64:ff9b::a00:1")]     // NAT64 of 10.0.0.1
    [DataRow("2002:7f00:1::")]      // 6to4 of 127.0.0.1
    [DataRow("2002:a9fe:a9fe::")]   // 6to4 of 169.254.169.254
    [DataRow("2001::1")]            // Teredo
    [DataRow("2001:db8::1")]        // documentation
    public void Policy_blocks_non_public_addresses(string ip)
    {
        Assert.IsTrue(OutboundAddressPolicy.IsBlocked(IPAddress.Parse(ip)), ip);
    }

    [DataTestMethod]
    [DataRow("93.184.216.34")]
    [DataRow("8.8.8.8")]
    [DataRow("1.1.1.1")]
    [DataRow("9.255.255.255")]
    [DataRow("11.0.0.1")]
    [DataRow("100.63.255.255")]
    [DataRow("100.128.0.1")]
    [DataRow("169.253.0.1")]
    [DataRow("172.15.255.255")]
    [DataRow("172.32.0.1")]
    [DataRow("192.0.1.1")]
    [DataRow("192.0.3.1")]
    [DataRow("192.167.0.1")]
    [DataRow("192.169.0.1")]
    [DataRow("198.17.255.255")]
    [DataRow("198.20.0.1")]
    [DataRow("223.255.255.255")]
    [DataRow("2606:2800:220:1:248:1893:25c8:1946")]
    [DataRow("2001:4860:4860::8888")]
    [DataRow("::ffff:93.184.216.34")]
    [DataRow("64:ff9b::5db8:d822")]  // NAT64 of 93.184.216.34
    [DataRow("2002:5db8:d822::")]    // 6to4 of 93.184.216.34
    public void Policy_allows_public_addresses(string ip)
    {
        Assert.IsFalse(OutboundAddressPolicy.IsBlocked(IPAddress.Parse(ip)), ip);
    }

    [DataTestMethod]
    [DataRow("localhost")]
    [DataRow("LOCALHOST.")]
    [DataRow("foo.localhost")]
    [DataRow("printer.local")]
    [DataRow("box.localdomain")]
    [DataRow("metadata.google.internal")]
    [DataRow("rss-reader-api")]       // single label: only resolvable inside the hosting network
    [DataRow("router.home.arpa")]
    [DataRow("")]
    public void Policy_blocks_internal_host_names(string host)
    {
        Assert.IsTrue(OutboundAddressPolicy.IsBlockedHostName(host), host);
    }

    [DataTestMethod]
    [DataRow("example.com")]
    [DataRow("feeds.example.co.uk")]
    [DataRow("localhost.example.com")]
    [DataRow("internal.example.com")]
    public void Policy_allows_public_host_names(string host)
    {
        Assert.IsFalse(OutboundAddressPolicy.IsBlockedHostName(host), host);
    }

    [TestMethod]
    public async Task Guard_allows_public_host()
    {
        var guard = new OutboundUrlGuard(Resolver(("example.com", "93.184.216.34")));
        await guard.ValidateAsync(new Uri("https://example.com/feed.xml"));
    }

    [TestMethod]
    public async Task Guard_rejects_host_resolving_to_private_address()
    {
        var guard = new OutboundUrlGuard(Resolver(("evil.example.com", "10.0.0.5")));
        await Assert.ThrowsExceptionAsync<BlockedOutboundUrlException>(
            () => guard.ValidateAsync(new Uri("https://evil.example.com/feed.xml")));
    }

    [TestMethod]
    public async Task Guard_rejects_host_with_mixed_public_and_private_answers()
    {
        var guard = new OutboundUrlGuard((_, _) => Task.FromResult(new[] { Public, IPAddress.Parse("169.254.169.254") }));
        await Assert.ThrowsExceptionAsync<BlockedOutboundUrlException>(
            () => guard.ValidateAsync(new Uri("https://rebind.example.com/")));
    }

    [DataTestMethod]
    [DataRow("http://127.0.0.1/")]
    [DataRow("http://169.254.169.254/metadata/instance")]
    [DataRow("http://[::1]/")]
    [DataRow("http://[::ffff:127.0.0.1]/")]
    [DataRow("http://2130706433/")]      // decimal spelling of 127.0.0.1
    [DataRow("http://0x7f000001/")]      // hex spelling of 127.0.0.1
    [DataRow("http://0177.0.0.1/")]      // octal first octet
    [DataRow("http://localhost:8080/")]
    [DataRow("http://rss-reader-api/")]
    [DataRow("http://metadata.google.internal/")]
    public async Task Guard_rejects_literal_and_internal_hosts_without_resolving(string url)
    {
        // Resolver throws for everything: these must be rejected before any lookup.
        var guard = new OutboundUrlGuard((_, _) => throw new InvalidOperationException("resolver must not be called"));
        await Assert.ThrowsExceptionAsync<BlockedOutboundUrlException>(
            () => guard.ValidateAsync(new Uri(url)), url);
    }

    [DataTestMethod]
    [DataRow("file:///etc/passwd")]
    [DataRow("ftp://example.com/feed.xml")]
    [DataRow("gopher://example.com/")]
    public async Task Guard_rejects_non_http_schemes(string url)
    {
        var guard = new OutboundUrlGuard(AlwaysPublic);
        await Assert.ThrowsExceptionAsync<BlockedOutboundUrlException>(
            () => guard.ValidateAsync(new Uri(url)), url);
    }

    [TestMethod]
    public async Task Guard_lets_unresolvable_hosts_through_to_the_real_fetch()
    {
        // Not a policy violation; the request itself will fail with the usual error.
        var guard = new OutboundUrlGuard(Resolver());
        await guard.ValidateAsync(new Uri("https://does-not-exist.example.com/"));
    }

    /// <summary>Primary handler that answers from a script and records every URI it was asked for.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<Uri, HttpResponseMessage> script;
        public List<Uri> Requested { get; } = new();

        public ScriptedHandler(Func<Uri, HttpResponseMessage> script) => this.script = script;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requested.Add(request.RequestUri);
            return Task.FromResult(this.script(request.RequestUri));
        }
    }

    private static HttpClient BuildClient(ScriptedHandler primary, Func<string, CancellationToken, Task<IPAddress[]>> resolver)
    {
        var services = new ServiceCollection();
        services
            .AddSingleton(new OutboundUrlGuard(resolver))
            .AddTransient<RedirectDowngradeHandler>()
            .AddTransient<OutboundUrlGuardHandler>()
            .AddHttpClient("RssClient")
            .AddHttpMessageHandler<RedirectDowngradeHandler>()
            .AddHttpMessageHandler<OutboundUrlGuardHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => primary);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("RssClient");
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }

    [TestMethod]
    public async Task Pipeline_blocks_redirect_from_public_host_to_metadata_endpoint()
    {
        var primary = new ScriptedHandler(uri =>
            uri.Host == "feed.example.com"
                ? Redirect("http://169.254.169.254/metadata/instance?api-version=2021-02-01")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("secret") });
        var client = BuildClient(primary, Resolver(("feed.example.com", "93.184.216.34")));

        await Assert.ThrowsExceptionAsync<BlockedOutboundUrlException>(
            () => client.GetAsync("https://feed.example.com/rss"));

        // The first hop was allowed; the metadata endpoint was never contacted.
        Assert.AreEqual(1, primary.Requested.Count);
        Assert.AreEqual("feed.example.com", primary.Requested[0].Host);
    }

    [TestMethod]
    public async Task Pipeline_blocks_redirect_to_host_that_resolves_private()
    {
        var primary = new ScriptedHandler(uri =>
            uri.Host == "feed.example.com"
                ? Redirect("https://internal.example.com/")
                : new HttpResponseMessage(HttpStatusCode.OK));
        var client = BuildClient(primary, Resolver(
            ("feed.example.com", "93.184.216.34"),
            ("internal.example.com", "192.168.0.10")));

        await Assert.ThrowsExceptionAsync<BlockedOutboundUrlException>(
            () => client.GetAsync("https://feed.example.com/rss"));
        Assert.AreEqual(1, primary.Requested.Count);
    }

    [TestMethod]
    public async Task Pipeline_follows_redirect_between_public_hosts()
    {
        var primary = new ScriptedHandler(uri =>
            uri.Host == "feed.example.com"
                ? Redirect("https://cdn.example.net/rss.xml")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<rss/>") });
        var client = BuildClient(primary, Resolver(
            ("feed.example.com", "93.184.216.34"),
            ("cdn.example.net", "151.101.1.1")));

        using var response = await client.GetAsync("https://feed.example.com/rss");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, primary.Requested.Count);
        Assert.AreEqual("cdn.example.net", primary.Requested[1].Host);
    }

    [TestMethod]
    public async Task Pipeline_blocks_direct_request_to_loopback()
    {
        var primary = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = BuildClient(primary, AlwaysPublic);

        await Assert.ThrowsExceptionAsync<BlockedOutboundUrlException>(
            () => client.GetAsync("http://127.0.0.1:8080/api/admin/stats"));
        Assert.AreEqual(0, primary.Requested.Count);
    }
}

[TestClass]
public sealed class GuardedConnectNetworkTests
{
    // Opens real sockets, so it is categorised like the other network tests and
    // excluded from CI; run deliberately with: dotnet test --filter TestCategory=Network
    [TestMethod]
    [TestCategory("Network")]
    public async Task Connect_callback_reaches_public_host_and_refuses_loopback()
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback = GuardedConnect.ConnectAsync };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        using var ok = await client.GetAsync("https://example.com/");
        Assert.IsTrue((int)ok.StatusCode < 500, $"example.com answered {(int)ok.StatusCode}");

        var ex = await Assert.ThrowsExceptionAsync<HttpRequestException>(
            () => client.GetAsync("http://127.0.0.1:1/"));
        Assert.IsInstanceOfType(ex.InnerException ?? ex, typeof(BlockedOutboundUrlException), ex.ToString());
    }
}
