using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RssReader.Server.Services;

namespace RssReaderTests;

[TestClass]
public class WriterStartupGateTests
{
    private static readonly string[] Origins = { "http://localhost:4280" };
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool PortAccepts(int port)
    {
        try
        {
            using var c = new TcpClient();
            c.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException) { return false; }
    }

    [TestMethod]
    public async Task Gate_AnswersHealthzWith200()
    {
        await using var gate = await WriterStartupGate.StartAsync(Array.Empty<string>(), Origins, "http://127.0.0.1:0", CancellationToken.None);
        using var http = new HttpClient { BaseAddress = new Uri(gate.Addresses.Single()) };

        var res = await http.GetAsync("/api/healthz");

        Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
        StringAssert.Contains(await res.Content.ReadAsStringAsync(), "seeding");
        await gate.StopAsync();
    }

    [TestMethod]
    public async Task Gate_WithReadyCallback_Healthz503UntilReadyThen200()
    {
        var ready = false;
        await using var gate = await WriterStartupGate.StartAsync(Array.Empty<string>(), Origins, "http://127.0.0.1:0", CancellationToken.None, () => ready);
        using var http = new HttpClient { BaseAddress = new Uri(gate.Addresses.Single()) };

        var res = await http.GetAsync("/api/healthz");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        StringAssert.Contains(await res.Content.ReadAsStringAsync(), "restoring");

        ready = true;
        res = await http.GetAsync("/api/healthz");
        Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
        await gate.StopAsync();
    }

    [TestMethod]
    public async Task Gate_AnswersOtherPathsWith503RetryAfterAndCorsForAllowedOrigin()
    {
        await using var gate = await WriterStartupGate.StartAsync(Array.Empty<string>(), Origins, "http://127.0.0.1:0", CancellationToken.None);
        using var http = new HttpClient { BaseAddress = new Uri(gate.Addresses.Single()) };

        foreach (var path in new[] { "/api/feeds", "/auth/me", "/" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Add("Origin", "http://localhost:4280");
            var res = await http.SendAsync(req);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, res.StatusCode, path);
            Assert.AreEqual("5", res.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(), path);
            Assert.AreEqual("http://localhost:4280", res.Headers.GetValues("Access-Control-Allow-Origin").Single(), path);
            Assert.AreEqual("true", res.Headers.GetValues("Access-Control-Allow-Credentials").Single(), path);
            Assert.AreEqual("Origin", res.Headers.Vary.Single(), path);
        }
        await gate.StopAsync();
    }

    [TestMethod]
    public async Task Gate_OmitsCorsHeadersForUnknownOrigin()
    {
        await using var gate = await WriterStartupGate.StartAsync(Array.Empty<string>(), Origins, "http://127.0.0.1:0", CancellationToken.None);
        using var http = new HttpClient { BaseAddress = new Uri(gate.Addresses.Single()) };

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/feeds");
        req.Headers.Add("Origin", "https://evil.example");
        var res = await http.SendAsync(req);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.IsFalse(res.Headers.Contains("Access-Control-Allow-Origin"));
        await gate.StopAsync();
    }

    [TestMethod]
    public async Task Gate_StopFreesThePortForTheRealHost()
    {
        var gate = await WriterStartupGate.StartAsync(Array.Empty<string>(), Origins, "http://127.0.0.1:0", CancellationToken.None);
        var port = new Uri(gate.Addresses.Single()).Port;
        Assert.IsTrue(PortAccepts(port));

        await gate.StopAsync();
        await gate.DisposeAsync();

        Assert.IsFalse(PortAccepts(port), "Gate no longer listening");
        // The real host must be able to bind the same port right after.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    private static async Task<HttpResponseMessage> WaitForGate(HttpClient http, string path)
    {
        for (var i = 0; i < 100; i++)
        {
            try { return await http.GetAsync(path); } catch (HttpRequestException) { await Task.Delay(50); }
        }
        throw new AssertFailedException("Gate did not come up");
    }

    [TestMethod]
    public async Task RunAsync_ServesProbeWhileRestoringThenStops()
    {
        var port = FreePort();
        var url = $"http://127.0.0.1:{port}";
        var restoreDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = WriterStartupGate.RunAsync(
            Array.Empty<string>(), _ => restoreDone.Task, Origins, NullLoggerFactory.Instance, url);

        // Gate is up: probe passes while the restore is still running.
        using var http = new HttpClient { BaseAddress = new Uri(url) };
        var probe = await WaitForGate(http, "/api/healthz");
        Assert.AreEqual(HttpStatusCode.OK, probe.StatusCode);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/api/feeds")).StatusCode);
        Assert.IsFalse(run.IsCompleted);

        restoreDone.SetResult(true);
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.IsFalse(PortAccepts(port), "Gate stopped and freed the port");
    }

    [TestMethod]
    public async Task RunAsync_CancelledWhileRestoring_StopsAndFreesPort()
    {
        var port = FreePort();
        using var cts = new CancellationTokenSource();

        var run = WriterStartupGate.RunAsync(
            Array.Empty<string>(), async ct => { await Task.Delay(Timeout.Infinite, ct); return true; },
            Origins, NullLoggerFactory.Instance, $"http://127.0.0.1:{port}", cts.Token);

        // Cancel only once the gate is observably listening (i.e. it is in the restore
        // phase), not on a wall-clock timer that can fire while the listener is still
        // starting under a loaded test runner.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!PortAccepts(port) && !run.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.IsTrue(PortAccepts(port), "gate should be listening before it is cancelled");
        cts.Cancel();

        try
        {
            await run;
            Assert.Fail("RunAsync should surface the cancellation");
        }
        catch (OperationCanceledException)
        {
            // TaskCanceledException or its base: either is the documented outcome.
        }

        Assert.IsFalse(PortAccepts(port));
    }

    [TestMethod]
    public async Task RunAsync_RestoreFails_ThrowsAndFreesPort()
    {
        var port = FreePort();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => WriterStartupGate.RunAsync(
            Array.Empty<string>(), _ => Task.FromResult(false), Origins, NullLoggerFactory.Instance, $"http://127.0.0.1:{port}"));

        Assert.IsFalse(PortAccepts(port));
    }
}
