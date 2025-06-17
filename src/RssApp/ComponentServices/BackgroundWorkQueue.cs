using System.Threading.Channels;

namespace RssApp.ComponentServices;
public class BackgroundWorkQueue
{
    private const int MaxQueueSize = 1000;
    private readonly Channel<Func<CancellationToken, Task>> _queue;

    public BackgroundWorkQueue()
    {
        var options = new BoundedChannelOptions(MaxQueueSize)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(options);
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