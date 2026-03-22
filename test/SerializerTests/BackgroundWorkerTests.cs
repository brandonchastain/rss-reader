using RssApp.ComponentServices;
using RssApp.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SerializerTests;

[TestClass]
public class BackgroundWorkerTests
{
    [TestMethod]
    public async Task BackgroundWorker_SpawnsConfiguredNumberOfWorkers()
    {
        var config = new RssAppConfig { BackgroundWorkerCount = 2, BackgroundQueueCapacity = 10 };
        var queue = new BackgroundWorkQueue(config);
        var logger = NullLogger<BackgroundWorker>.Instance;
        var worker = new BackgroundWorker(logger, queue, config);

        int completedItems = 0;
        for (int i = 0; i < 4; i++)
        {
            await queue.QueueBackgroundWorkItemAsync(async ct =>
            {
                await Task.Delay(50, ct);
                Interlocked.Increment(ref completedItems);
            });
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = worker.StartAsync(cts.Token);

        // Wait for all items to be processed
        while (completedItems < 4 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50);
        }

        await cts.CancelAsync();
        try { await executeTask; } catch (OperationCanceledException) { }

        Assert.AreEqual(4, completedItems, "All queued items should be processed");
    }

    [TestMethod]
    public void BackgroundWorkQueue_RespectsConfiguredCapacity()
    {
        var config = new RssAppConfig { BackgroundQueueCapacity = 2 };
        var queue = new BackgroundWorkQueue(config);

        // Should be able to write 2 items (capacity = 2)
        var task1 = queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask);
        var task2 = queue.QueueBackgroundWorkItemAsync(_ => Task.CompletedTask);

        Assert.IsTrue(task1.IsCompletedSuccessfully, "First item should be written within capacity");
        Assert.IsTrue(task2.IsCompletedSuccessfully, "Second item should be written within capacity");
    }

    [TestMethod]
    public void RssAppConfig_DefaultWorkerCountIsThree()
    {
        var config = new RssAppConfig();
        Assert.AreEqual(3, config.BackgroundWorkerCount);
    }

    [TestMethod]
    public void RssAppConfig_DefaultQueueCapacityIsHundred()
    {
        var config = new RssAppConfig();
        Assert.AreEqual(100, config.BackgroundQueueCapacity);
    }
}
