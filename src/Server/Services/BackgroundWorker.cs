namespace RssApp.ComponentServices;
public class BackgroundWorker : BackgroundService
{
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
        _logger.LogInformation("BackgroundService is starting.");

        int workerCount = 5; // Set your desired concurrency here
        var tasks = new List<Task>();
        for (int i = 0; i < workerCount; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var workItem = await _queue.DequeueAsync(stoppingToken);
                    await workItem(stoppingToken);
                }
            }, stoppingToken));
        }

        await Task.WhenAll(tasks);

        _logger.LogInformation("BackgroundService is stopping.");
    }
}