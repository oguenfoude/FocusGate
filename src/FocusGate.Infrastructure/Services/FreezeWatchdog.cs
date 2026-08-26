using FocusGate.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

/// <summary>
/// Safety net against process-wide stalls. If EVERY registered heartbeat
/// component goes silent for the stale threshold while the host is still
/// running, dumps the stale component list into the log (FATAL) and stops
/// the application lifetime so Program.cs rebuilds a fresh host.
/// Requires two consecutive confirmations to avoid one-off false positives.
/// </summary>
public class FreezeWatchdog : BackgroundService
{
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WarmupGrace = TimeSpan.FromMinutes(2);

    private readonly ILogger<FreezeWatchdog> _log;
    private readonly IHostApplicationLifetime _lifetime;
    private int _consecutiveStaleChecks;

    public FreezeWatchdog(ILogger<FreezeWatchdog> log, IHostApplicationLifetime lifetime)
    {
        _log = log;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Freeze watchdog armed — stall threshold {Threshold}, check every {Interval}",
            StaleThreshold, CheckInterval);

        var startedUtc = DateTime.UtcNow;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(CheckInterval, stoppingToken);

                if (Heartbeat.Count == 0)
                {
                    _consecutiveStaleChecks = 0;
                    continue; // nothing registered yet — loops still starting
                }

                if (DateTime.UtcNow - startedUtc < WarmupGrace && Heartbeat.Count < 3)
                    continue; // give staggered startup time to register components

                var stale = Heartbeat.Stale(StaleThreshold);
                if (stale.Count == 0 || stale.Count < Heartbeat.Count)
                {
                    _consecutiveStaleChecks = 0;
                    continue; // at least one loop is alive — not a process-wide freeze
                }

                _consecutiveStaleChecks++;
                if (_consecutiveStaleChecks < 2)
                    continue;

                var detail = string.Join(", ", stale.Take(20).Select(s => $"{s.Name} idle {(int)s.Age.TotalSeconds}s"));
                _log.LogCritical("FREEZE detected: all {Count} heartbeat components silent for over {Threshold}. Forcing clean restart. Stale: {Detail}",
                    Heartbeat.Count, StaleThreshold, detail);

                try
                {
                    RestartService.IsRestarting = true;
                }
                catch { }

                _lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Freeze watchdog crashed — self-restart protection is OFF until next host rebuild");
        }
    }
}
