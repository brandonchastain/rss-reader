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

        while (!stoppingToken.IsCancellationRequested)
        {
            var workItem = await _queue.DequeueAsync(stoppingToken);
            await workItem(stoppingToken);
        }

        _logger.LogInformation("BackgroundService is stopping.");
    }
}