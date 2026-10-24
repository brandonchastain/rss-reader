using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Config;
using RssReader.Server.Services;

namespace RssReaderTests;

[TestClass]
public class LitestreamSupervisorTests
{
    /// <summary>
    /// Program.cs registers the supervisor as a singleton and again as a hosted service, so
    /// DI disposes the same instance twice at shutdown. The second call must not throw
    /// (it used to: ObjectDisposedException from CancelAsync on the disposed CTS).
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var supervisor = new LitestreamSupervisor(new DurabilityConfig(), "/tmp/storage.db", NullLogger<LitestreamSupervisor>.Instance);
        await supervisor.StartAsync(CancellationToken.None);
        await supervisor.StopAsync(CancellationToken.None);

        await supervisor.DisposeAsync();
        await supervisor.DisposeAsync();
    }
}
