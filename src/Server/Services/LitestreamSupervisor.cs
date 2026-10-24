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
        public async Task<(LitestreamSyncResult Result, string Output)> SyncAsync(
            string dbPath, string socketPath, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                var (code, output) = await _run(_binary, $"sync -wait -timeout {seconds} -socket \"{socketPath}\" \"{dbPath}\"", cts.Token);
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
    /// Stop order (spec 3.3): registered after the feed scheduler and worker, so they have
    /// stopped writing before the final sync; registered after the file backup service too,
    /// so this stops <em>before</em> the share copy runs and the two budgets add up inside
    /// the host's shutdown timeout rather than overlap. First <c>litestream sync -wait</c>
    /// uploads the pending WAL (logged as "final sync OK"), then the child gets SIGTERM and
    /// performs its own shutdown sync as a safety net (11.4) before exiting.
    ///
    /// An exited child is restarted with backoff; replication is a shadow of the share in
    /// rollout steps 1 and 2, so a Litestream failure is logged, never fatal.
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
                _logger.LogError("Litestream exited unexpectedly (code {Code}); restarting in {Backoff}. Replication is paused meanwhile; the share backup still runs.", process.ExitCode, backoff);
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

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!Enabled) return;
            await _stopping.CancelAsync();
            var process = _process;
            if (process == null || process.HasExited)
            {
                _logger.LogWarning("Shutdown: Litestream was not running; nothing to sync.");
                return;
            }

            // Spec 3.3 step 4: upload what is pending now, bounded.
            var sw = Stopwatch.StartNew();
            var (result, output) = await _cli.SyncAsync(_dbPath, _socketPath, _config.FinalSyncTimeout, cancellationToken);
            switch (result)
            {
                case LitestreamSyncResult.Ok:
                    _logger.LogInformation("Shutdown: Litestream final sync OK in {Ms} ms.", sw.ElapsedMilliseconds);
                    break;
                case LitestreamSyncResult.TimedOut:
                    _logger.LogError("Shutdown: Litestream final sync timed out after {Timeout}; up to one sync interval of writes may be missing from the replica.", _config.FinalSyncTimeout);
                    break;
                default:
                    _logger.LogError("Shutdown: Litestream final sync failed: {Output}", output);
                    break;
            }

            // Then let the daemon run its own shutdown sync and exit.
            Signal(process, 15 /* SIGTERM */);
            try
            {
                using var exitWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitWait.CancelAfter(_config.FinalSyncTimeout);
                await process.WaitForExitAsync(exitWait.Token);
                _logger.LogInformation("Litestream exited (code {Code}) {Ms} ms into shutdown.", process.ExitCode, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Litestream did not exit within {Timeout} of SIGTERM; killing it.", _config.FinalSyncTimeout);
                try { process.Kill(entireProcessTree: true); } catch { }
            }
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
