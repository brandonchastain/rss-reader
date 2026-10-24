#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RssApp.Config;

namespace RssReader.Server.Services
{
    /// <summary>
    /// What the new writer needs from <c>litestream restore -f</c>: start it, know when the
    /// initial restore is done, read how far it has applied, stop it. Behind an interface so
    /// the handover sequencer can be tested with the process faked.
    /// </summary>
    public interface ILitestreamFollower : IAsyncDisposable
    {
        /// <summary>Path the follower writes; renamed over the live path once caught up.</summary>
        string OutputPath { get; }
        void Start();
        /// <summary>True when the process is gone (restore failed, replica missing, or stopped).</summary>
        bool HasExited { get; }
        int? ExitCode { get; }
        /// <summary>
        /// Transaction id the followed copy is at, from the <c>OUTPUT-txid</c> sidecar
        /// Litestream 0.5 keeps beside it (16 hex digits). Null until the initial restore
        /// has completed.
        /// </summary>
        long? ReadTxid();
        /// <summary>SIGTERM and wait; the follower exits 0 and leaves a clean database (no -wal/-shm).</summary>
        Task<int?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
    }

    /// <summary>
    /// <c>litestream restore -config CFG -f -follow-interval I -o OUT DB</c>: full restore of
    /// the newest snapshot plus LTX tail, then polling the replica and applying new LTX
    /// files as they land (verified against 0.5.17: the sidecar <c>OUT-txid</c> holds the
    /// applied txid; SIGTERM exits 0 and removes the WAL sidecars; quick_check passes).
    /// </summary>
    public sealed class LitestreamFollower : ILitestreamFollower
    {
        private readonly DurabilityConfig _config;
        private readonly string _dbPath;
        private readonly ILogger _logger;
        private Process? _process;

        public string OutputPath { get; }
        public string TxidPath => OutputPath + "-txid";

        public LitestreamFollower(DurabilityConfig config, string dbPath, string outputPath, ILogger logger)
        {
            _config = config;
            _dbPath = dbPath;
            OutputPath = outputPath;
            _logger = logger;
        }

        public bool HasExited => _process == null || _process.HasExited;
        public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;

        public void Start()
        {
            SafeDelete(OutputPath); SafeDelete(TxidPath); SafeDelete(OutputPath + "-wal"); SafeDelete(OutputPath + "-shm");
            var interval = FormatGoDuration(_config.FollowInterval);
            var psi = new ProcessStartInfo(_config.LitestreamBinary,
                $"restore -config \"{_config.LitestreamConfigPath}\" -f -follow-interval {interval} -o \"{OutputPath}\" \"{_dbPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) _logger.LogInformation("litestream follow: {Line}", e.Data); };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.Contains("level=ERROR", StringComparison.Ordinal)) _logger.LogError("litestream follow: {Line}", e.Data);
                else _logger.LogInformation("litestream follow: {Line}", e.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _logger.LogInformation("Follower started (pid {Pid}) at {Utc}: restoring the replica to {Output} and tailing it every {Interval}.",
                process.Id, DateTimeOffset.UtcNow.ToString("O"), OutputPath, interval);
        }

        public long? ReadTxid() => ReadTxidFile(TxidPath);

        public static long? ReadTxidFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path).Trim();
                return long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public async Task<int?> StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var process = _process;
            if (process == null) return null;
            if (!process.HasExited)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    kill(process.Id, 15 /* SIGTERM */);
                else
                    try { process.Kill(); } catch { }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);
                try { await process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Follower did not exit within {Timeout} of SIGTERM; killing it.", timeout);
                    try { process.Kill(entireProcessTree: true); } catch { }
                    try { await process.WaitForExitAsync(cancellationToken); } catch (OperationCanceledException) { }
                }
            }
            return process.HasExited ? process.ExitCode : null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is { } p)
            {
                if (!p.HasExited) { try { p.Kill(entireProcessTree: true); } catch { } }
                try { await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); } catch { }
                p.Dispose();
            }
        }

        internal static string FormatGoDuration(TimeSpan t) =>
            t.TotalSeconds >= 1 && t.TotalMilliseconds % 1000 == 0
                ? $"{(int)t.TotalSeconds}s"
                : $"{(int)t.TotalMilliseconds}ms";

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);
    }
}
