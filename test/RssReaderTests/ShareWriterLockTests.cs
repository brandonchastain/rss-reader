using Microsoft.Extensions.Logging.Abstractions;
using RssReader.Server.Services;

namespace RssReaderTests;

[TestClass]
public class ShareWriterLockTests
{
    private string _dir = null!;
    private string _lockPath = null!;
    private readonly List<ShareWriterLock> _locks = new();

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ShareWriterLockTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lockPath = Path.Combine(_dir, "storage.db.lock");
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var l in _locks) l.Release();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // Generous stale threshold relative to the heartbeat so timing never flips a test.
    private ShareWriterLock NewLock(
        string? path = null, TimeSpan? stale = null, TimeSpan? heartbeat = null, TimeSpan? giveUp = null)
    {
        var l = new ShareWriterLock(
            path ?? _lockPath,
            stale ?? TimeSpan.FromSeconds(30),
            heartbeat ?? TimeSpan.FromSeconds(30),
            NullLogger.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            GiveUpAfter = giveUp ?? TimeSpan.FromSeconds(10),
        };
        _locks.Add(l);
        return l;
    }

    private void WriteForeignLock(TimeSpan age)
    {
        File.WriteAllText(_lockPath, "other-host:1:now");
        File.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow - age);
    }

    [TestMethod]
    public void TryAcquire_OnEmptyDirectory_CreatesFile()
    {
        var l = NewLock();
        Assert.IsFalse(l.IsHeld);
        Assert.IsTrue(l.TryAcquire());
        Assert.IsTrue(l.IsHeld);
        Assert.IsTrue(File.Exists(_lockPath));
        Assert.IsTrue(l.IsAlive(out _));
    }

    [TestMethod]
    public void TryAcquire_IsIdempotentForTheHolder()
    {
        var l = NewLock();
        Assert.IsTrue(l.TryAcquire());
        Assert.IsTrue(l.TryAcquire());
        Assert.IsTrue(l.IsHeld);
    }

    [TestMethod]
    public void TryAcquire_FailsWhileFreshForeignLockExists()
    {
        WriteForeignLock(TimeSpan.Zero);
        var l = NewLock();
        Assert.IsFalse(l.TryAcquire(out var error));
        Assert.IsNull(error, "Contention is not an error");
        Assert.IsFalse(l.IsHeld);
        Assert.AreEqual("other-host:1:now", File.ReadAllText(_lockPath), "Foreign lock untouched");
    }

    [TestMethod]
    public void TryAcquire_TakesOverLockOlderThanStaleAfter()
    {
        WriteForeignLock(TimeSpan.FromMinutes(5));
        var l = NewLock(stale: TimeSpan.FromMinutes(1));
        Assert.IsTrue(l.TryAcquire());
        Assert.IsTrue(l.IsHeld);
        Assert.AreNotEqual("other-host:1:now", File.ReadAllText(_lockPath), "Stale lock replaced by ours");
        Assert.IsTrue(l.IsAlive(out var age));
        Assert.IsTrue(age < TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public void IsAlive_ReportsAgeAndStaleness()
    {
        var l = NewLock(stale: TimeSpan.FromSeconds(10));
        Assert.IsFalse(l.IsAlive(out _), "No file: not alive");

        WriteForeignLock(TimeSpan.FromSeconds(5));
        Assert.IsTrue(l.IsAlive(out var age));
        Assert.IsTrue(age >= TimeSpan.FromSeconds(4) && age < TimeSpan.FromSeconds(10), age.ToString());

        WriteForeignLock(TimeSpan.FromSeconds(60));
        Assert.IsFalse(l.IsAlive(out _), "Older than StaleAfter: not alive");
    }

    [TestMethod]
    public void Heartbeat_AdvancesFileMtime()
    {
        var l = NewLock();
        Assert.IsTrue(l.TryAcquire());
        var old = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        File.SetLastWriteTimeUtc(_lockPath, old);

        l.Heartbeat();

        var mtime = File.GetLastWriteTimeUtc(_lockPath);
        Assert.IsTrue(mtime > old + TimeSpan.FromMinutes(9), $"mtime {mtime:O} should be ~now");
    }

    [TestMethod]
    public void Heartbeat_RecreatesFileIfItWentMissing()
    {
        var l = NewLock();
        Assert.IsTrue(l.TryAcquire());
        File.Delete(_lockPath);
        l.Heartbeat();
        Assert.IsTrue(File.Exists(_lockPath));
    }

    [TestMethod]
    public void Heartbeat_WhenNotHeld_DoesNothing()
    {
        var l = NewLock();
        l.Heartbeat();
        Assert.IsFalse(File.Exists(_lockPath));
    }

    [TestMethod]
    public async Task HeartbeatLoop_KeepsFileFreshAcrossSeveralIntervals()
    {
        // The loop must keep touching the file. Observed as the mtime advancing at
        // least twice; no assertion rides on the loop meeting a latency bound, since a
        // loaded test runner can starve a background task for over a second.
        var l = NewLock(heartbeat: TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(l.TryAcquire());
        var seen = File.GetLastWriteTimeUtc(_lockPath);
        var advances = 0;

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (advances < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
            var now = File.GetLastWriteTimeUtc(_lockPath);
            if (now > seen) { advances++; seen = now; }
        }

        Assert.AreEqual(2, advances, "Heartbeat loop touched the file repeatedly");
        Assert.IsTrue(l.IsAlive(out _));
    }

    [TestMethod]
    public void Release_DeletesFileAndClearsIsHeld()
    {
        var l = NewLock();
        Assert.IsTrue(l.TryAcquire());
        l.Release();
        Assert.IsFalse(l.IsHeld);
        Assert.IsFalse(File.Exists(_lockPath));
    }

    [TestMethod]
    public void Release_IsIdempotent()
    {
        var l = NewLock();
        l.Release();                     // never held
        Assert.IsTrue(l.TryAcquire());
        l.Release();
        l.Release();                     // second release: no throw, nothing to delete
        Assert.IsFalse(l.IsHeld);
        Assert.IsFalse(File.Exists(_lockPath));
    }

    [TestMethod]
    public void Release_DoesNotDeleteAForeignLockWhenNotHeld()
    {
        WriteForeignLock(TimeSpan.Zero);
        var l = NewLock();
        l.Release();
        Assert.IsTrue(File.Exists(_lockPath));
    }

    [TestMethod]
    public async Task ReleasedLock_StopsHeartbeating()
    {
        var l = NewLock(heartbeat: TimeSpan.FromMilliseconds(30));
        Assert.IsTrue(l.TryAcquire());
        l.Release();
        // A foreign writer takes the path; our stopped loop must not touch it.
        WriteForeignLock(TimeSpan.FromMinutes(5));
        var before = File.GetLastWriteTimeUtc(_lockPath);
        await Task.Delay(150);
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(_lockPath));
    }

    [TestMethod]
    public void Dispose_Releases()
    {
        var l = NewLock();
        Assert.IsTrue(l.TryAcquire());
        l.Dispose();
        Assert.IsFalse(l.IsHeld);
        Assert.IsFalse(File.Exists(_lockPath));
    }

    [TestMethod]
    public async Task AcquireAsync_WaitsWhileFreshLockExists_ProceedsOnceDeleted()
    {
        WriteForeignLock(TimeSpan.Zero);
        var l = NewLock();

        var acquire = l.AcquireAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.IsFalse(acquire.IsCompleted, "Still waiting on the live foreign lock");
        Assert.IsFalse(l.IsHeld);

        File.Delete(_lockPath);
        Assert.IsTrue(await acquire.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(l.IsHeld);
        Assert.IsTrue(File.Exists(_lockPath));
    }

    [TestMethod]
    public async Task AcquireAsync_ProceedsImmediatelyOnStaleLock()
    {
        WriteForeignLock(TimeSpan.FromMinutes(5));
        var l = NewLock(stale: TimeSpan.FromMinutes(1));
        Assert.IsTrue(await l.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(l.IsHeld);
    }

    [TestMethod]
    public async Task AcquireAsync_HonoursCancellation()
    {
        WriteForeignLock(TimeSpan.Zero);
        var l = NewLock();
        using var cts = new CancellationTokenSource(150);

        // Either the poll delay (TaskCanceledException) or the loop's own check
        // (OperationCanceledException) observes the token first; both are cancellation.
        try
        {
            await l.AcquireAsync(cts.Token);
            Assert.Fail("AcquireAsync returned instead of honouring cancellation");
        }
        catch (OperationCanceledException) { }

        Assert.IsFalse(l.IsHeld);
        Assert.IsTrue(File.Exists(_lockPath), "Foreign lock untouched");
    }

    [TestMethod]
    public async Task AcquireAsync_AlreadyCancelled_ThrowsBeforeTouchingAnything()
    {
        var l = NewLock();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => l.AcquireAsync(cts.Token));
        Assert.IsFalse(File.Exists(_lockPath));
    }

    [TestMethod]
    public async Task AcquireAsync_UnwritableDirectory_GivesUpAfterBoundedWaitWithoutHolding()
    {
        // A directory that does not exist is the portable "cannot create the file" case.
        var missing = Path.Combine(_dir, "missing", "storage.db.lock");
        var l = NewLock(path: missing, giveUp: TimeSpan.FromMilliseconds(200));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var held = await l.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsFalse(held, "Gave up instead of holding");
        Assert.IsFalse(l.IsHeld);
        Assert.IsTrue(sw.Elapsed >= TimeSpan.FromMilliseconds(150), "Retried for about GiveUpAfter first");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5), "Bounded");
    }

    [TestMethod]
    public void TryAcquire_UnwritableDirectory_ReportsError()
    {
        var l = NewLock(path: Path.Combine(_dir, "missing", "storage.db.lock"));
        Assert.IsFalse(l.TryAcquire(out var error));
        Assert.IsNotNull(error);
        Assert.IsFalse(l.IsHeld);
    }

    [TestMethod]
    public void TwoInstances_ContendingForSamePath_NeverBothHold()
    {
        // Many rounds of simultaneous claims on an empty path: CreateNew makes exactly
        // one winner per round.
        for (var round = 0; round < 25; round++)
        {
            var a = NewLock();
            var b = NewLock();
            bool ra = false, rb = false;
            using var start = new ManualResetEventSlim(false);
            var ta = Task.Run(() => { start.Wait(); ra = a.TryAcquire(); });
            var tb = Task.Run(() => { start.Wait(); rb = b.TryAcquire(); });
            start.Set();
            Task.WaitAll(ta, tb);

            Assert.IsTrue(ra ^ rb, $"Round {round}: exactly one must win (a={ra}, b={rb})");
            Assert.IsFalse(a.IsHeld && b.IsHeld);
            Assert.IsTrue(File.Exists(_lockPath));

            (ra ? a : b).Release();
            Assert.IsFalse(File.Exists(_lockPath));
        }
    }

    [TestMethod]
    public void TryAcquire_SucceedsImmediatelyAfterAnotherHolderReleases()
    {
        // Existence is probed with an open, not a stat, so a release is visible to the
        // next TryAcquire with no poll or cache delay in between (on the Azure Files
        // CIFS mount a stat is answered from a 30 s attribute cache; see ProbeExists).
        var first = NewLock();
        var second = NewLock();
        Assert.IsTrue(first.TryAcquire());
        Assert.IsFalse(second.TryAcquire(out var error), "Fresh foreign lock blocks");
        Assert.IsNull(error);

        first.Release();

        Assert.IsTrue(second.TryAcquire(out error), "Released lock is claimable at once");
        Assert.IsNull(error);
        Assert.IsTrue(second.IsHeld);
        Assert.IsFalse(first.IsHeld);
        Assert.IsTrue(File.Exists(_lockPath));
    }

    [TestMethod]
    public async Task TwoInstances_SecondWaitsUntilFirstReleases()
    {
        // Liveness of the heartbeat is covered by HeartbeatLoop_*; here the stale
        // window is wide so only an explicit release can let the second one in.
        var first = NewLock();
        var second = NewLock();
        Assert.IsTrue(first.TryAcquire());

        var wait = second.AcquireAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.IsFalse(wait.IsCompleted, "Second waits while the first holds the lock");
        Assert.IsFalse(second.IsHeld);

        first.Release();
        Assert.IsTrue(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(second.IsHeld);
        Assert.IsFalse(first.IsHeld);
    }
}
