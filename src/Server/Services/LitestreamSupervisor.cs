#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RssApp.Config;

namespace RssReader.Server.Services
{
    /// <summary>Outcome of one <c>litestream sync</c> invocation (BLOB-DURABILITY-SPEC.md 3.3 step 4).</summary>
    public enum LitestreamSyncResult { Ok, Failed, TimedOut }

    /// <summary>
    /// The <c>litestream</c> command line, with the process behind an injectable runner so
    /// tests can fake exit codes and hangs (spec 9.1, "Litestream process wrapper").
    /// </summary>
    public sealed class LitestreamCli
    {
        /// <summary>Runs the binary with the arguments; returns the exit code and combined output. Must honour the token.</summary>
        public delegate Task<(int ExitCode, string Output)> Runner(string fileName, string arguments, CancellationToken cancellationToken);

        private readonly string _binary;
        private readonly Runner _run;

        public LitestreamCli(string binary, Runner? runner = null)
        {
            _binary = binary;
            _run = runner ?? RunProcessAsync;
        }

        /// <summary>
        /// <c>litestream sync -wait -timeout N -socket PATH DB</c>: asks the running daemon
        /// to upload the pending WAL now and returns when the upload is done. Bounded by
        /// <paramref name="timeout"/>; the daemon side has the same budget.
        /// </summary>
        public async Task<(LitestreamSyncResult Result, string Output, long? Txid)> SyncAsync(
            string dbPath, string socketPath, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                var (code, output) = await _run(_binary, $"sync -wait -timeout {seconds} -socket \"{socketPath}\" -json \"{dbPath}\"", cts.Token);
                return (code == 0 ? LitestreamSyncResult.Ok : LitestreamSyncResult.Failed, output, code == 0 ? ParseSyncTxid(output) : null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (LitestreamSyncResult.TimedOut, string.Empty, null);
            }
        }

        /// <summary>
        /// The replica's transaction id from <c>sync -json</c> output
        /// (<c>{"db_path":..., "txid": N, "replica_txid": N, "duration_ms": N}</c>, decimal in 0.5.17).
        /// Prefers <c>replica_txid</c>, which is what a restore can reach.
        /// </summary>
        public static long? ParseSyncTxid(string output)
        {
            var m = System.Text.RegularExpressions.Regex.Match(output, @"""replica_txid""\s*:\s*""?(\d+)""?");
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(output, @"""txid""\s*:\s*""?(\d+)""?");
            return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : null;
        }

        /// <summary>
        /// <c>litestream restore -config CFG -o OUT DB</c>: a full restore of the replica that
        /// <paramref name="configPath"/> declares for <paramref name="dbPath"/>, written to
        /// <paramref name="outputPath"/> (which must not exist yet; Litestream refuses to
        /// overwrite). Spec 3.2 step 4. Bounded by <paramref name="timeout"/>.
        /// </summary>
        public async Task<(LitestreamSyncResult Result, string Output)> RestoreAsync(
            string configPath, string dbPath, string outputPath, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                var (code, output) = await _run(_binary, $"restore -config \"{configPath}\" -o \"{outputPath}\" \"{dbPath}\"", cts.Token);
                return (code == 0 ? LitestreamSyncResult.Ok : LitestreamSyncResult.Failed, output);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (LitestreamSyncResult.TimedOut, string.Empty);
            }
        }

        public static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            return (process.ExitCode, (await stdout + await stderr).Trim());
        }
    }

    /// <summary>
    /// Runs <c>litestream replicate</c> as a child of the app for the life of the host
    /// (spec 3.1). The app, not the entrypoint, starts it: the startup gate seeds
    /// <c>/tmp/storage.db</c> by renaming a staged copy into place, and Litestream must
    /// open the file only after that (verification 11.3). SIGTERM therefore reaches the
    /// app directly as PID 1, and this service stops the child in its own
    /// <see cref="StopAsync"/>.
    ///
    /// Stop order (spec 3.3; hosted services stop in reverse registration order, see the
    /// registrations in Program.cs): the feed scheduler, worker and retention service stop
    /// first, so nothing is writing during the final sync; this service stops next; then
    /// the blob lease is released. First
    /// <c>litestream sync -wait</c> uploads the pending WAL (logged as "final sync OK"),
    /// then the child gets SIGTERM and performs its own shutdown sync as a safety net
    /// (11.4) before exiting. The next writer restores from the replica only once it holds
    /// the lease, so everything this sync uploaded is in that restore.
    ///
    /// An exited child is restarted with backoff; a Litestream failure is logged, never fatal,
    /// and the recovery point grows until it is back (the replica is the only durable copy).
    /// </summary>
    public sealed class LitestreamSupervisor : IHostedService, IAsyncDisposable
    {
        private readonly DurabilityConfig _config;
        private readonly string _dbPath;
        private readonly ILogger _logger;
        private readonly LitestreamCli _cli;
        private readonly CancellationTokenSource _stopping = new();
        private Process? _process;
        private Task? _supervise;
        private readonly string _socketPath;
        private bool _disposed;

        public bool Enabled => _config.LitestreamEnabled;
        public bool IsRunning => _process is { HasExited: false };
        public int Restarts { get; private set; }

        public LitestreamSupervisor(DurabilityConfig config, string dbPath, ILogger<LitestreamSupervisor> logger, LitestreamCli? cli = null)
        {
            _config = config;
            _dbPath = dbPath;
            _logger = logger;
            _cli = cli ?? new LitestreamCli(config.LitestreamBinary);
            // Litestream's control socket; enabled in litestream.yml with this path.
            _socketPath = "/tmp/litestream.sock";
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!Enabled) return Task.CompletedTask;
            _supervise = Task.Run(SuperviseAsync, CancellationToken.None);
            return Task.CompletedTask;
        }

        private async Task SuperviseAsync()
        {
            var backoff = TimeSpan.FromSeconds(2);
            while (!_stopping.IsCancellationRequested)
            {
                Process process;
                try
                {
                    process = StartChild();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Litestream could not be started ({Binary}); retrying in {Backoff}.", _config.LitestreamBinary, backoff);
                    try { await Task.Delay(backoff, _stopping.Token); } catch (OperationCanceledException) { break; }
                    backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
                    continue;
                }

                _process = process;
                _logger.LogInformation("Litestream replicate started (pid {Pid}, config {Config}) for {Db}.", process.Id, _config.LitestreamConfigPath, _dbPath);
                try { await process.WaitForExitAsync(_stopping.Token); }
                catch (OperationCanceledException) { break; }

                if (_stopping.IsCancellationRequested) break;
                Restarts++;
                _logger.LogError("Litestream exited unexpectedly (code {Code}); restarting in {Backoff}. Replication is paused meanwhile; writes since the last sync are only on the container disk.", process.ExitCode, backoff);
                try { await Task.Delay(backoff, _stopping.Token); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
        }

        private Process StartChild()
        {
            var psi = new ProcessStartInfo(_config.LitestreamBinary, $"replicate -config \"{_config.LitestreamConfigPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) _logger.LogInformation("litestream: {Line}", e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) LogLitestreamLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        private void LogLitestreamLine(string line)
        {
            // Litestream writes structured logs to stderr; keep its level.
            if (line.Contains("level=ERROR", StringComparison.Ordinal)) _logger.LogError("litestream: {Line}", line);
            else if (line.Contains("level=WARN", StringComparison.Ordinal)) _logger.LogWarning("litestream: {Line}", line);
            else _logger.LogInformation("litestream: {Line}", line);
        }

        private bool _finalSyncDone;
        private long? _finalTxid;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!Enabled) return;
            if (_finalSyncDone)
            {
                _logger.LogInformation("Shutdown: Litestream already synced and stopped at the handoff; nothing to do.");
                await _stopping.CancelAsync();
                return;
            }
            await SyncAndStopAsync("Shutdown", cancellationToken);
        }

        /// <summary>
        /// Spec 3.3 step 4, used both at host shutdown and at a cooperative handoff: one
        /// <c>litestream sync -wait</c>, then SIGTERM to the child and wait for it. Returns the
        /// replica transaction id the sync reported, when it did. Runs once; later calls
        /// return the first result.
        /// </summary>
        public async Task<long?> SyncAndStopAsync(string phase, CancellationToken cancellationToken)
        {
            if (!Enabled) return null;
            if (_finalSyncDone) return _finalTxid;
            _finalSyncDone = true;
            await _stopping.CancelAsync();
            var process = _process;
            if (process == null || process.HasExited)
            {
                _logger.LogWarning("{Phase}: Litestream was not running; nothing to sync.", phase);
                return null;
            }

            // Upload what is pending now, bounded.
            var sw = Stopwatch.StartNew();
            var (result, output, txid) = await _cli.SyncAsync(_dbPath, _socketPath, _config.FinalSyncTimeout, cancellationToken);
            switch (result)
            {
                case LitestreamSyncResult.Ok:
                    _finalTxid = txid;
                    _logger.LogInformation("{Phase}: Litestream final sync OK in {Ms} ms at {Utc} (replica txid {Txid}); the blob lease is released next.",
                        phase, sw.ElapsedMilliseconds, DateTimeOffset.UtcNow.ToString("O"), txid?.ToString() ?? "unknown");
                    break;
                case LitestreamSyncResult.TimedOut:
                    _logger.LogError("{Phase}: Litestream final sync timed out after {Timeout}; up to one sync interval of writes may be missing from the replica.", phase, _config.FinalSyncTimeout);
                    break;
                default:
                    _logger.LogError("{Phase}: Litestream final sync failed: {Output}", phase, output);
                    break;
            }

            // Then let the daemon run its own shutdown sync and exit.
            Signal(process, 15 /* SIGTERM */);
            try
            {
                using var exitWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitWait.CancelAfter(_config.FinalSyncTimeout);
                await process.WaitForExitAsync(exitWait.Token);
                _logger.LogInformation("{Phase}: Litestream exited (code {Code}) {Ms} ms in.", phase, process.ExitCode, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("{Phase}: Litestream did not exit within {Timeout} of SIGTERM; killing it.", phase, _config.FinalSyncTimeout);
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            return _finalTxid;
        }

        private static void Signal(Process process, int signal)
        {
            if (RuntimeInterop.IsPosix)
            {
                RuntimeInterop.kill(process.Id, signal);
            }
            else
            {
                try { process.Kill(); } catch { }
            }
        }

        /// <summary>
        /// Idempotent: Program.cs registers the instance both as a singleton and as a hosted
        /// service, so the container disposes it twice at shutdown. The second call is a no-op
        /// (the first already disposed <c>_stopping</c>).
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _stopping.CancelAsync();
            // VSTHRD003: no synchronization context in ASP.NET, so awaiting the supervise
            // task cannot deadlock.
#pragma warning disable VSTHRD003
            if (_supervise != null) { try { await _supervise; } catch { } }
#pragma warning restore VSTHRD003
            _process?.Dispose();
            _stopping.Dispose();
        }

        private static class RuntimeInterop
        {
            public static bool IsPosix => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

            [DllImport("libc", SetLastError = true)]
            public static extern int kill(int pid, int sig);
        }
    }
}
