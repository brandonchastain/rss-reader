using RssApp.Config;

namespace RssApp.ComponentServices;

using RssApp.Config;

public class BackgroundWorker : BackgroundService
{
    private readonly ILogger<BackgroundWorker> _logger;
    private readonly BackgroundWorkQueue _queue;
    private readonly int _workerCount;

    public BackgroundWorker(
        ILogger<BackgroundWorker> logger,
        BackgroundWorkQueue queue,
        RssAppConfig config)
    {
        _logger = logger;
        _queue = queue;
        _workerCount = config.BackgroundWorkerCount;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting {WorkerCount} background workers", _workerCount);

        var tasks = new List<Task>();

        for (int i = 0; i < _workerCount; i++)
        {
            tasks.Add(DoWorkAsync(stoppingToken));
        }

        await Task.WhenAll(tasks);
    }

    private async Task DoWorkAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var workItem = await _queue.DequeueAsync(stoppingToken);
            await workItem(stoppingToken);
        }
    }
}