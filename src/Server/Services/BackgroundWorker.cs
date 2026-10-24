using RssApp.Config;

namespace RssApp.ComponentServices;

using RssApp.Config;

public class BackgroundWorker : BackgroundService
{
    private readonly ILogger<BackgroundWorker> _logger;
    private readonly BackgroundWorkQueue _queue;
    private readonly int _workerCount;
    private readonly RssReader.Server.Services.WriterState? _writerState;

    public BackgroundWorker(
        ILogger<BackgroundWorker> logger,
        BackgroundWorkQueue queue,
        RssAppConfig config,
        RssReader.Server.Services.WriterState? writerState = null)
    {
        _logger = logger;
        _queue = queue;
        _workerCount = config.BackgroundWorkerCount;
        _writerState = writerState;
    }

    protected override async Task ExecuteAsync(CancellationToken hostStopping)
    {
        _logger.LogInformation("Starting {WorkerCount} background workers", _workerCount);

        // Also stops at a cooperative handoff (WriterState.WritersStopping). Exits cleanly
        // on either token: an exception here would make the host stop itself.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            hostStopping, _writerState?.WritersStopping ?? CancellationToken.None);
        var stoppingToken = linked.Token;

        var tasks = new List<Task>();

        for (int i = 0; i < _workerCount; i++)
        {
            tasks.Add(DoWorkAsync(stoppingToken));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
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