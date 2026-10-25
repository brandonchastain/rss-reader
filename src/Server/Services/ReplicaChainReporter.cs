#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RssApp.Config;

namespace RssReader.Server.Services
{
    /// <summary>
    /// Logs the shape of the Litestream replica so it can be read from Log Analytics (the
    /// owner's az user has no blob data-plane access): files, MB and txid range per level from
    /// <c>litestream ltx -level all -json</c>, and what a restore would read from
    /// <c>litestream restore -dry-run</c>. Runs a minute after start and every ten minutes.
    /// BLOB-DURABILITY-SPEC.md rollout step 3: the restore chain must stay one snapshot plus a
    /// few minutes of tail, and levels 2 and 3 must write files.
    /// </summary>
    public sealed class ReplicaChainReporter : BackgroundService
    {
        public sealed record LevelSummary(int Level, int Files, long Bytes, long MinTxid, long MaxTxid, DateTimeOffset Newest);

        private readonly DurabilityConfig _config;
        private readonly string _dbPath;
        private readonly LitestreamCli.Runner _run;
        private readonly ILogger _logger;
        public TimeSpan FirstDelay { get; init; } = TimeSpan.FromMinutes(1);
        public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(10);

        public ReplicaChainReporter(DurabilityConfig config, string dbPath, ILogger<ReplicaChainReporter> logger, LitestreamCli.Runner? run = null)
        {
            _config = config;
            _dbPath = dbPath;
            _logger = logger;
            _run = run ?? LitestreamCli.RunProcessAsync;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_config.LitestreamEnabled) return;
            try
            {
                await Task.Delay(FirstDelay, stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await ReportOnceAsync(stoppingToken);
                    await Task.Delay(Interval, stoppingToken);
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task ReportOnceAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                var (code, json) = await _run(_config.LitestreamBinary, $"ltx -config \"{_config.LitestreamConfigPath}\" -level all -json \"{_dbPath}\"", cts.Token);
                if (code != 0) { _logger.LogWarning("Replica chain: litestream ltx failed: {Output}", json); return; }
                var levels = ParseLevels(json);
                var plan = await RestorePlanAsync(cts.Token);
                _logger.LogInformation(
                    "Replica chain at {Utc}: {Levels}. A restore would read {Plan}.",
                    DateTimeOffset.UtcNow.ToString("O"),
                    string.Join("; ", levels.Select(l => $"L{l.Level} {l.Files} files {l.Bytes / 1048576.0:F1} MB txid {l.MinTxid}-{l.MaxTxid} newest {l.Newest:HH:mm:ss}")),
                    plan);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Replica chain: report failed.");
            }
        }

        private async Task<string> RestorePlanAsync(CancellationToken ct)
        {
            var scratch = $"/tmp/replica-chain-plan-{Guid.NewGuid():N}.db";
            var (code, output) = await _run(_config.LitestreamBinary, $"restore -config \"{_config.LitestreamConfigPath}\" -o \"{scratch}\" -dry-run \"{_dbPath}\"", ct);
            if (code != 0) return $"nothing (dry-run failed: {output.Split('\n').FirstOrDefault()?.Trim()})";
            var (files, bytes) = ParseRestorePlan(output);
            return $"{files} files, {bytes / 1048576.0:F1} MB";
        }

        public static List<LevelSummary> ParseLevels(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var result = new List<LevelSummary>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            static long Hex(JsonElement e, string n) => long.Parse(e.GetProperty(n).GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            foreach (var g in doc.RootElement.EnumerateArray().GroupBy(e => e.GetProperty("level").GetInt32()).OrderBy(g => g.Key))
            {
                result.Add(new LevelSummary(
                    g.Key, g.Count(), g.Sum(e => e.GetProperty("size").GetInt64()),
                    g.Min(e => Hex(e, "min_txid")), g.Max(e => Hex(e, "max_txid")),
                    g.Max(e => e.GetProperty("timestamp").GetDateTimeOffset())));
            }
            return result;
        }

        /// <summary>Counts the files and bytes in the "Files to fetch" table of <c>restore -dry-run</c>.</summary>
        public static (int Files, long Bytes) ParseRestorePlan(string output)
        {
            var files = 0;
            long bytes = 0;
            foreach (Match m in Regex.Matches(output, @"^\s*\d\s+[0-9a-f]{16}-[0-9a-f]{16}\.ltx\s+[0-9a-f]{16}\s+[0-9a-f]{16}\s+(\d+)\s", RegexOptions.Multiline))
            {
                files++;
                bytes += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            return (files, bytes);
        }
    }
}
