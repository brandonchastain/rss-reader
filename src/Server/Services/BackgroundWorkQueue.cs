using System.Threading.Channels;

namespace RssApp.ComponentServices;
public class BackgroundWorkQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _queue =
        Channel.CreateBounded<Func<CancellationToken, Task>>(1000); // Limit to 1000 concurrent work items (feeds being refreshed)

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