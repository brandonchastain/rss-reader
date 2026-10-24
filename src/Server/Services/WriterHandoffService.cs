#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RssReader.Server.Services
{
    /// <summary>
    /// Polls the handoff channel while this writer holds the lease and runs
    /// <see cref="WriterHandover"/> when the next revision asks. A plain loop rather than a
    /// hook on lease renewal so the check interval (2 s) is independent of the renewal
    /// interval (10 s); a blob GET every 2 s is noise next to Litestream's traffic.
    /// </summary>
    public sealed class WriterHandoffService : BackgroundService
    {
        private readonly WriterHandover? _handover;
        private readonly Func<bool> _leaseHeld;
        private readonly TimeSpan _poll;
        private readonly ILogger _logger;

        public WriterHandoffService(WriterHandover? handover, Func<bool> leaseHeld, TimeSpan poll, ILogger<WriterHandoffService> logger)
        {
            _handover = handover;
            _leaseHeld = leaseHeld;
            _poll = poll;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_handover == null) return;
            _logger.LogInformation("Handoff watcher running as {Self}; checking every {Poll}.", _handover.Identity, _poll);
            var consecutiveErrors = 0;
            while (!stoppingToken.IsCancellationRequested && !_handover.HandedOver)
            {
                try
                {
                    await Task.Delay(_poll, stoppingToken);
                    if (!_leaseHeld()) continue;
                    if (await _handover.PollAsync(stoppingToken)) break;
                    consecutiveErrors = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (++consecutiveErrors <= 3 || consecutiveErrors % 30 == 0)
                        _logger.LogWarning(ex, "Handoff watcher: poll failed ({Count} in a row).", consecutiveErrors);
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            if (_handover?.AfterReleaseTask is { } after && !after.IsCompleted)
            {
                _logger.LogInformation("Shutdown: waiting for the post-handoff share copy to finish.");
                try { await after.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
            }
        }
    }
}
