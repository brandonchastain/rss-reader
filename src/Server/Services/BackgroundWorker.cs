namespace RssApp.ComponentServices;

public class BackgroundWorker : BackgroundService
{
    private const int WorkerCount = 5;
    private readonly ILogger<BackgroundWorker> _logger;
    private readonly BackgroundWorkQueue _queue;

    public BackgroundWorker(
        ILogger<BackgroundWorker> logger,
        BackgroundWorkQueue queue)
    {
        _logger = logger;
        _queue = queue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>();

        for (int i = 0; i < WorkerCount; i++)
        {
            tasks.Add(DoWorkAsync(stoppingToken));
        }

        var allDone = Task.WhenAll(tasks);
        var maxWait = TimeSpan.FromMinutes(1);
        await Task.WhenAny(allDone, Task.Delay(maxWait, stoppingToken));
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