using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Config;
using RssReader.Server.Services;

namespace RssReaderTests;

/// <summary>
/// The replica-shape report (rollout step 3 finish line: levels 2 and 3 write files, the
/// restore chain is one snapshot plus a short tail). Inputs are real litestream 0.5.17 outputs.
/// </summary>
[TestClass]
public class ReplicaChainReporterTests
{
    private const string LtxJson = @"[
  { ""level"": 0, ""min_txid"": ""0000000000000003"", ""max_txid"": ""0000000000000003"", ""size"": 217, ""timestamp"": ""2026-10-24T23:10:23Z"" },
  { ""level"": 0, ""min_txid"": ""0000000000000004"", ""max_txid"": ""0000000000000004"", ""size"": 224, ""timestamp"": ""2026-10-24T23:10:24Z"" },
  { ""level"": 1, ""min_txid"": ""0000000000000001"", ""max_txid"": ""0000000000000004"", ""size"": 600, ""timestamp"": ""2026-10-24T23:10:23Z"" },
  { ""level"": 9, ""min_txid"": ""0000000000000001"", ""max_txid"": ""0000000000000001"", ""size"": 567, ""timestamp"": ""2026-10-24T23:10:21Z"" }
]";

    private const string DryRun = @"Restore plan:
  source: /work/run3/a.db
  target: /work/run3/r.db
  replica: file
  txid range: 0000000000000001 - 000000000000005c

Files to fetch:
level  file                                   min_txid          max_txid          size      timestamp
9      0000000000000001-0000000000000001.ltx  0000000000000001  0000000000000001  40781492  2026-10-24T23:39:31Z
3      0000000000000001-0000000000000014.ltx  0000000000000001  0000000000000014  41197405  2026-10-24T23:39:55Z
0      000000000000004c-000000000000004c.ltx  000000000000004c  000000000000004c  11852     2026-10-24T23:42:48Z
";

    [TestMethod]
    public void ParseLevels_GroupsByLevel_WithCountsBytesAndTxidRange()
    {
        var levels = ReplicaChainReporter.ParseLevels(LtxJson);

        CollectionAssert.AreEqual(new[] { 0, 1, 9 }, levels.Select(l => l.Level).ToArray());
        var l0 = levels[0];
        Assert.AreEqual(2, l0.Files);
        Assert.AreEqual(441L, l0.Bytes);
        Assert.AreEqual(3L, l0.MinTxid);
        Assert.AreEqual(4L, l0.MaxTxid);
        Assert.AreEqual(600L, levels[1].Bytes);
    }

    [TestMethod]
    public void ParseLevels_EmptyReplica_NoLevels()
    {
        Assert.AreEqual(0, ReplicaChainReporter.ParseLevels("[]").Count);
    }

    [TestMethod]
    public void ParseRestorePlan_CountsFilesAndBytesOfTheFetchTable()
    {
        var (files, bytes) = ReplicaChainReporter.ParseRestorePlan(DryRun);
        Assert.AreEqual(3, files);
        Assert.AreEqual(40781492L + 41197405L + 11852L, bytes);
    }

    [TestMethod]
    public async Task ReportOnce_RunsLtxAndDryRun_AndDoesNotThrowOnFailure()
    {
        var calls = new List<string>();
        var reporter = new ReplicaChainReporter(
            new DurabilityConfig { LitestreamEnabled = true }, "/tmp/storage.db", NullLogger<ReplicaChainReporter>.Instance,
            (file, args, ct) =>
            {
                calls.Add(args);
                return Task.FromResult(args.StartsWith("ltx") ? (0, LtxJson) : (0, DryRun));
            });
        await reporter.ReportOnceAsync(CancellationToken.None);
        Assert.AreEqual(2, calls.Count);
        StringAssert.Contains(calls[0], "-level all -json");
        StringAssert.Contains(calls[1], "-dry-run");

        var failing = new ReplicaChainReporter(
            new DurabilityConfig { LitestreamEnabled = true }, "/tmp/storage.db", NullLogger<ReplicaChainReporter>.Instance,
            (file, args, ct) => throw new InvalidOperationException("no litestream"));
        await failing.ReportOnceAsync(CancellationToken.None);
    }
}
