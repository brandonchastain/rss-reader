using System.Threading.Channels;
using RssApp.Config;

namespace RssApp.ComponentServices;
public class BackgroundWorkQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _queue;

    public BackgroundWorkQueue(RssAppConfig config)
    {
        _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(config.BackgroundQueueCapacity);
    }

    public async Task QueueBackgroundWorkItemAsync(Func<CancellationToken, Task> workItem)
    {
        if (await _queue.Writer.WaitToWriteAsync())
        {
            await _queue.Writer.WriteAsync(workItem);
        }
    }

    public async Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }
}