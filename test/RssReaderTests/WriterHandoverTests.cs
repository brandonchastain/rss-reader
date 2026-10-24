using Microsoft.Extensions.Logging.Abstractions;
using RssReader.Server.Services;

namespace RssReaderTests;

/// <summary>
/// The running writer's side of the cooperative handover (BLOB-DURABILITY-SPEC.md 3.2 v2):
/// which markers it acts on, in what order it drains, and that the lease always moves.
/// </summary>
[TestClass]
public class WriterHandoverTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private const string Self = "rev-0095/replica-a";

    private sealed class Recorder
    {
        public List<string> Order { get; } = new();
        public IDictionary<string, string>? LeaseMetadata { get; private set; }
        public TaskCompletionSource AfterRelease { get; } = new();
        public long? SyncTxid { get; set; } = 42;
        public Exception? SyncThrows { get; set; }

        public WriterHandover.Steps Steps => new()
        {
            StopWritersAsync = _ => { Order.Add("stop-writers"); return Task.CompletedTask; },
            CheckpointAsync = _ => { Order.Add("checkpoint"); return Task.CompletedTask; },
            FinalSyncAsync = _ =>
            {
                Order.Add("sync");
                if (SyncThrows != null) throw SyncThrows;
                return Task.FromResult(SyncTxid);
            },
            ReleaseLeaseAsync = m => { Order.Add("release"); LeaseMetadata = m; return Task.CompletedTask; },
            AfterReleaseAsync = _ => { Order.Add("after"); AfterRelease.TrySetResult(); return Task.CompletedTask; },
        };
    }

    private static WriterHandover Make(InMemoryWriterHandoffChannel channel, Recorder rec, WriterState? state = null) =>
        new(state ?? new WriterState(), channel, rec.Steps, Self, Started, TimeSpan.FromSeconds(5), NullLogger.Instance);

    private static HandoffRequest Fresh(string by = "rev-0096/replica-b", string id = "req1") =>
        new(id, by, Started.AddMinutes(10));

    [TestMethod]
    public void IsForMe_FreshRequestFromAnotherWriter_True()
    {
        var h = Make(new InMemoryWriterHandoffChannel(), new Recorder());
        Assert.IsTrue(h.IsForMe(Fresh(), null));
    }

    [TestMethod]
    public void IsForMe_IgnoresOwnMarker_OlderMarkers_AcknowledgedMarkers_AndNone()
    {
        var h = Make(new InMemoryWriterHandoffChannel(), new Recorder());
        Assert.IsFalse(h.IsForMe(null, null), "no marker");
        Assert.IsFalse(h.IsForMe(Fresh(by: Self), null), "our own request from boot");
        Assert.IsFalse(h.IsForMe(new HandoffRequest("old", "rev-0094/x", Started.AddMinutes(-5)), null), "a previous deploy's marker");
        Assert.IsFalse(h.IsForMe(Fresh(id: "req1"), new HandoffAck("req1", "rev-0095/old", 7, Started.AddMinutes(11))), "already acknowledged");
        Assert.IsTrue(h.IsForMe(Fresh(id: "req2"), new HandoffAck("req1", "rev-0095/old", 7, Started.AddMinutes(11))), "ack for a different request");
    }

    [TestMethod]
    public async Task Poll_FreshRequest_DrainsInOrder_AcksWithTxid_ReleasesLeaseWithMetadata_RefusesWrites()
    {
        var channel = new InMemoryWriterHandoffChannel();
        channel.Seed(Fresh());
        var rec = new Recorder();
        var state = new WriterState();
        var h = Make(channel, rec, state);

        Assert.IsTrue(await h.PollAsync(CancellationToken.None));

        CollectionAssert.AreEqual(new[] { "stop-writers", "checkpoint", "sync", "release" }, rec.Order.Take(4).ToList());
        await rec.AfterRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("after", rec.Order.Last(), "the share copy runs after the lease is released");

        var (_, ack) = await channel.ReadAsync(CancellationToken.None);
        Assert.IsNotNull(ack);
        Assert.AreEqual("req1", ack!.Id);
        Assert.AreEqual(Self, ack.ReleasedBy);
        Assert.AreEqual(42L, ack.FinalTxid);
        Assert.AreEqual("42", rec.LeaseMetadata!["finaltxid"]);
        Assert.AreEqual("req1", rec.LeaseMetadata["handoffid"]);

        Assert.IsTrue(state.IsHandedOver);
        Assert.AreEqual("rev-0096/replica-b", state.HandedOverTo);
        Assert.AreEqual(42L, state.FinalTxid);
    }

    [TestMethod]
    public async Task Poll_RunsOnce_SecondPollIsNoOp()
    {
        var channel = new InMemoryWriterHandoffChannel();
        channel.Seed(Fresh());
        var rec = new Recorder();
        var h = Make(channel, rec);

        Assert.IsTrue(await h.PollAsync(CancellationToken.None));
        Assert.IsFalse(await h.PollAsync(CancellationToken.None));
        channel.Seed(Fresh(id: "req2", by: "rev-0097/c"));
        Assert.IsFalse(await h.PollAsync(CancellationToken.None), "a writer that handed over never drains again");
        Assert.AreEqual(1, rec.Order.Count(s => s == "sync"));
        Assert.AreEqual(1, channel.Acks);
    }

    [TestMethod]
    public async Task HandOver_SyncThrows_StillAcknowledgesWithoutTxidAndReleasesLease()
    {
        var channel = new InMemoryWriterHandoffChannel();
        channel.Seed(Fresh());
        var rec = new Recorder { SyncThrows = new InvalidOperationException("litestream missing") };
        var state = new WriterState();
        var h = Make(channel, rec, state);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => h.PollAsync(CancellationToken.None));

        Assert.IsTrue(rec.Order.Contains("release"), "the lease must move even when the sync fails");
        var (_, ack) = await channel.ReadAsync(CancellationToken.None);
        Assert.IsNotNull(ack);
        Assert.IsNull(ack!.FinalTxid);
        Assert.IsFalse(rec.LeaseMetadata!.ContainsKey("finaltxid"));
        Assert.IsTrue(state.IsHandedOver);
    }

    [TestMethod]
    public async Task HandOver_WritersHang_ContinuesAfterDrainTimeout()
    {
        var channel = new InMemoryWriterHandoffChannel();
        channel.Seed(Fresh());
        var order = new List<string>();
        var steps = new WriterHandover.Steps
        {
            StopWritersAsync = async ct => { order.Add("stop"); await Task.Delay(TimeSpan.FromSeconds(30), ct); },
            CheckpointAsync = _ => { order.Add("checkpoint"); return Task.CompletedTask; },
            FinalSyncAsync = _ => { order.Add("sync"); return Task.FromResult<long?>(1); },
            ReleaseLeaseAsync = _ => { order.Add("release"); return Task.CompletedTask; },
        };
        var h = new WriterHandover(new WriterState(), channel, steps, Self, Started, TimeSpan.FromMilliseconds(100), NullLogger.Instance);

        await h.PollAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "stop", "checkpoint", "sync", "release" }, order);
    }

    [TestMethod]
    public void HandoffMetadata_RoundTrips()
    {
        var req = Fresh();
        var ack = new HandoffAck("req1", Self, 123456789012L, Started.AddMinutes(11));
        var m = HandoffMetadata.FromRequest(req);
        foreach (var (k, v) in HandoffMetadata.FromAck(ack)) m[k] = v;

        var (r, a) = HandoffMetadata.Parse(m);
        Assert.AreEqual(req, r);
        Assert.AreEqual(ack, a);
    }

    [TestMethod]
    public void WriterState_MiddlewareRefusesWritesOnlyAfterHandoff()
    {
        var state = new WriterState();
        Assert.IsFalse(WriterState.IsMutating("GET"));
        Assert.IsFalse(WriterState.IsMutating("HEAD"));
        Assert.IsFalse(WriterState.IsMutating("OPTIONS"));
        Assert.IsTrue(WriterState.IsMutating("POST"));
        Assert.IsTrue(WriterState.IsMutating("DELETE"));
        Assert.IsTrue(state.MarkHandedOver("x", 1));
        Assert.IsFalse(state.MarkHandedOver("y", 2), "flips once");
        Assert.AreEqual("x", state.HandedOverTo);
    }

    [TestMethod]
    public async Task WriterState_Middleware_503ForPostAfterHandoff_PassesGet()
    {
        var state = new WriterState();
        var mw = WriterState.Middleware(state);
        var nextCalls = 0;
        Task Next(Microsoft.AspNetCore.Http.HttpContext _) { nextCalls++; return Task.CompletedTask; }

        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Response.Body = new MemoryStream();
        await mw(ctx, Next);
        Assert.AreEqual(1, nextCalls, "before the handoff everything passes");

        state.MarkHandedOver("rev-0096/b", 5);
        ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Response.Body = new MemoryStream();
        await mw(ctx, Next);
        Assert.AreEqual(1, nextCalls);
        Assert.AreEqual(503, ctx.Response.StatusCode);
        Assert.AreEqual("5", ctx.Response.Headers.RetryAfter.ToString());

        ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Method = "GET";
        await mw(ctx, Next);
        Assert.AreEqual(2, nextCalls, "reads keep working");
    }
}
