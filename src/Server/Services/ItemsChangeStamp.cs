using System;
using System.Threading;

namespace RssReader.Server.Services;

/// <summary>
/// Process-wide version stamp that moves whenever the Items table is written
/// (new items, read/saved/tag changes, deletes). The timeline's "new posts?" probe
/// uses it as an ETag so a browser revalidation can be answered with 304 and no
/// SQL while nothing has changed. Seeded from the clock so a restart never hands
/// out a stamp an old cached response already carries.
/// </summary>
public sealed class ItemsChangeStamp
{
    private long _value = DateTime.UtcNow.Ticks;

    public long Current => Volatile.Read(ref _value);

    public void Bump() => Interlocked.Increment(ref _value);
}
