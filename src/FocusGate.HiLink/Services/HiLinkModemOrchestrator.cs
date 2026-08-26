using System.Collections.Concurrent;
using FocusGate.Core.Enums;
using FocusGate.Core.Interfaces;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using FocusGate.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FocusGate.HiLink.Services;

public class HiLinkModemOrchestrator : BackgroundService
{
    private readonly int _maxModems;
    private readonly IServiceProvider _services;
    private readonly DatabaseWriteChannel _db;
    private readonly ILogger<HiLinkModemOrchestrator> _log;
    private readonly IConfigProvider _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MeetMobService _meetMob;
    private readonly ConcurrentDictionary<string, (ModemHandler handler, string imei)> _handlers = new();
    private readonly ConcurrentDictionary<string, byte> _activeImeis = new();
    private readonly ConcurrentDictionary<string, int> _blacklistedIps = new();
    private readonly HashSet<string> _knownModemIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noSimIps = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxIpFailures = 3;

    public HiLinkModemOrchestrator(IServiceProvider services, DatabaseWriteChannel db,
        ILogger<HiLinkModemOrchestrator> log, IConfigProvider config, ILoggerFactory loggerFactory)
    {
        _services = services;
        _db = db;
        _log = log;
        _config = config;
        _loggerFactory = loggerFactory;
        _maxModems = _config.Get("modem.max_count", 30);
        _meetMob = new MeetMobService(new MeetMobTokenStore(), loggerFactory.CreateLogger<MeetMobService>(), config);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var enabled = _config.Get("hilink.enabled", "true");
        if (!enabled.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("HiLink scanning DISABLED by config (hilink.enabled=false)");
            return;
        }

        _log.LogInformation("HiLink Orchestrator ready (max {Max} modems)", _maxModems);

        // Daily noon cache-void: runs in background, voids MeetMob token cache at 12:00pm every day
        _ = Task.Run(async () =>
        {
            try { await DailyNoonCacheVoidAsync(ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "DailyNoonCacheVoid crashed unexpectedly"); }
        }, ct);

        // Daily SMS cleanup: runs in background, deletes SMS records older than 60 days
        _ = Task.Run(async () =>
        {
            try { await DailySmsCleanupAsync(ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "DailySmsCleanup crashed unexpectedly"); }
        }, ct);

        var countBefore = _handlers.Count;
        while (!ct.IsCancellationRequested)
        {
            Heartbeat.Pulse("orchestrator.scan");
            try
            {
                _log.LogDebug("Scan cycle starting ({Count} handlers active, {Blacklisted} blacklisted IPs)", _handlers.Count, _blacklistedIps.Count);
                await ScanAsync(ct);
                if (_handlers.Count != countBefore)
                {
                    _log.LogInformation("Active modems: {Count} online ({Blacklisted} blacklisted IPs)", _handlers.Count, _blacklistedIps.Count);
                    countBefore = _handlers.Count;
                }
                else
                {
                    _log.LogDebug("Scan cycle complete ({Count} modems online, {Blacklisted} blacklisted IPs)", _handlers.Count, _blacklistedIps.Count);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Scan cycle error"); }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("HiLink Orchestrator stopped");
    }

    /// <summary>
    /// Fires once per day at exactly 12:00:00 AM (midnight).
    /// Voids the MeetMob token cache and clears any blocked IPs for fresh re-detection.
    /// Zero downtime, no restart required.
    /// </summary>
    private async Task DailyNoonCacheVoidAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Calculate time until next midnight (12:00:00 AM)
                var now = DateTime.Now;
                var nextMidnight = now.Date.AddDays(1); // always tomorrow's midnight

                var delay = nextMidnight - now;
                _log.LogInformation("Daily midnight cache void scheduled at {MidnightTime:yyyy-MM-dd HH:mm:ss} (in {Hours:0}h {Minutes:0}m)",
                    nextMidnight, delay.TotalHours, delay.Minutes);

                await Task.Delay(delay, ct);
                if (ct.IsCancellationRequested) break;

                _log.LogInformation("=== DAILY NOON CACHE VOID TRIGGERED ===");
                _log.LogInformation("Clearing MeetMob token cache for {Count} active modems...", _handlers.Count);

                // Void all MeetMob tokens → forces fresh OTP re-login on next balance check
                foreach (var kv in _handlers)
                {
                    try
                    {
                        var handler = kv.Value.handler;
                        var simInfo = await _db.GetActiveSimInfoAsync(handler.Context.ModemId);
                        var phone = MeetMobService.FormatPhone(simInfo.PhoneNumber);
                        if (!string.IsNullOrEmpty(phone))
                        {
                            await _meetMob.InvalidateTokenAsync(phone);
                            _log.LogInformation("  [VOID] MeetMob token cleared for ModemId={ModemId} Phone={Phone}", handler.Context.ModemId, phone);
                        }
                        else
                        {
                            _log.LogWarning("  [VOID] Skipped — no phone number for ModemId={ModemId}", handler.Context.ModemId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Failed to void token for modem {Key}", kv.Key);
                    }
                }

                // Clear blacklisted IPs so offline modems get a fresh probe
                var blacklistCount = _blacklistedIps.Count;
                _blacklistedIps.Clear();
                _noSimIps.Clear();

                _log.LogInformation("=== DAILY NOON CACHE VOID COMPLETE: {Count} tokens cleared, {Blacklist} IPs unblocked ===",
                    _handlers.Count, blacklistCount);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Daily noon cache void error — will retry tomorrow");
                // Wait 1 hour before retrying to avoid tight loop on persistent errors
                try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch { break; }
            }
        }
    }

    /// <summary>
    /// Daily SMS cleanup: deletes SMS records older than 60 days.
    /// Runs once per day at 3:00 AM.
    /// </summary>
    private async Task DailySmsCleanupAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var nextRun = now.Date.AddHours(3); // 3:00 AM daily
                if (nextRun <= now) nextRun = nextRun.AddDays(1);

                var delay = nextRun - now;
                _log.LogInformation("Daily SMS cleanup scheduled at {Time:yyyy-MM-dd HH:mm:ss} (in {Hours:0}h {Minutes:0}m)",
                    nextRun, delay.TotalHours, delay.TotalMinutes);

                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }

                _log.LogInformation("=== DAILY SMS CLEANUP STARTING ===");

                try
                {
                    await _db.EnqueueAsync(new DatabaseWriteChannel.WriteOperation
                    {
                        Type = DatabaseWriteChannel.Op.CleanupOldSms,
                        Data = new { }
                    });
                    _log.LogInformation("=== DAILY SMS CLEANUP COMPLETE ===");
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Daily SMS cleanup error — will retry tomorrow");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Daily SMS cleanup loop error — will retry in 1 hour");
                try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch { break; }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("HiLink Orchestrator shutting down, disposing {Count} handlers...", _handlers.Count);

        var handlers = _handlers.Values.Select(v => v.handler).ToList();
        _handlers.Clear();
        _activeImeis.Clear();

        foreach (var handler in handlers)
        {
            try { handler.Dispose(); }
            catch (Exception ex) { _log.LogWarning(ex, "Error disposing handler"); }
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        foreach (var kv in _handlers.Where(kv => !kv.Value.handler.IsAlive))
        {
            var (handler, imei) = kv.Value;
            _log.LogWarning("{Ip}: Handler dead, freeing IMEI {IMEI} — will re-probe next cycle", kv.Key, imei);
            _handlers.TryRemove(kv.Key, out _);
            _activeImeis.TryRemove(imei, out _);
            _knownModemIps.Add(kv.Key);
            _blacklistedIps.TryRemove(kv.Key, out _);
            try { handler.Dispose(); } catch { }
        }

        var staleNoSim = _noSimIps.Where(ip => !_handlers.ContainsKey(ip)).ToList();
        foreach (var ip in staleNoSim) _noSimIps.Remove(ip);

        bool startedNewHandlers = false;

        if (_handlers.Count >= _maxModems) return;

        var ipsRaw = _config.Get("hilink.scan_ips", "");
        string[] discoveredIps;

        if (!string.IsNullOrWhiteSpace(ipsRaw))
        {
            discoveredIps = ipsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            discoveredIps = HiLinkDiscovery.DiscoverGatewayIps();
        }

        var allIps = discoveredIps.Concat(_knownModemIps)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var toScan = allIps.Where(ip =>
        {
            if (_handlers.ContainsKey(ip)) return false;
            if (_noSimIps.Contains(ip)) return false;

            if (_blacklistedIps.TryGetValue(ip, out var failCount))
            {
                if (failCount >= MaxIpFailures && !_knownModemIps.Contains(ip))
                    return false;

                if (_knownModemIps.Contains(ip))
                    return true;

                return false;
            }

            return true;
        }).ToArray();

        if (toScan.Length == 0) return;

        using var scope = _services.CreateScope();
        var discoveryLog = scope.ServiceProvider.GetRequiredService<ILogger<HiLinkDiscovery>>();
        var discovery = new HiLinkDiscovery(discoveryLog);
        var probeTimeout = int.TryParse(_config.Get("hilink.probe_timeout_ms", "2000"), out var t) ? t : 2000;
        var devices = await discovery.DiscoverAsync(toScan, probeTimeout);

        var foundIps = new HashSet<string>(devices.Select(d => d.Ip), StringComparer.OrdinalIgnoreCase);
        foreach (var ip in toScan)
        {
            if (!foundIps.Contains(ip))
            {
                if (_blacklistedIps.TryGetValue(ip, out var count))
                {
                    var newCount = count + 1;
                    _blacklistedIps[ip] = newCount;

                    if (_knownModemIps.Contains(ip))
                        _log.LogWarning("{Ip}: Known modem probe failed ({Count}/{Max}) — will retry", ip, newCount, MaxIpFailures);
                    else if (newCount >= MaxIpFailures)
                        _log.LogWarning("{Ip}: Permanently blacklisted after {Count} failures — never connected", ip, newCount);
                    else
                        _log.LogWarning("{Ip}: Probe failed ({Count}/{Max}) — blacklisted", ip, newCount, MaxIpFailures);
                }
                else
                {
                    _blacklistedIps[ip] = 1;

                    if (_knownModemIps.Contains(ip))
                        _log.LogWarning("{Ip}: Known modem probe failed (1/{Max}) — will retry", ip, MaxIpFailures);
                    else
                        _log.LogWarning("{Ip}: Probe failed (1/{Max}) — blacklisted", ip, MaxIpFailures);
                }
            }
            else
            {
                _blacklistedIps.TryRemove(ip, out _);
                _knownModemIps.Add(ip);
            }
        }

        var processTasks = devices.Select(async device =>
        {
            if (_handlers.Count >= _maxModems) return;

            if (!string.IsNullOrEmpty(device.Imei) && _activeImeis.ContainsKey(device.Imei))
            {
                return;
            }

            try
            {
                var hilink = new HiLinkCommandService(_loggerFactory.CreateLogger<HiLinkCommandService>(), _config);

                await hilink.OpenAsync(device.Ip);

                if (!await hilink.IsAliveAsync())
                {
                    _log.LogWarning("{Ip}: Alive check failed", device.Ip);
                    try { hilink.Dispose(); } catch { }
                    return;
                }

                var imei = await hilink.GetImeiAsync();
                if (string.IsNullOrEmpty(imei))
                {
                    imei = device.Imei;
                }
                if (string.IsNullOrEmpty(imei) || imei.StartsWith("HILINK-", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning("{Ip}: No real IMEI available (got '{IMEI}'), skipping modem", device.Ip, imei);
                    try { hilink.Dispose(); } catch { }
                    return;
                }

                if (!_activeImeis.TryAdd(imei, 0))
                {
                    try { hilink.Dispose(); } catch { }
                    return;
                }

                var imsi = await hilink.GetImsiAsync();
                var manufacturer = device.Manufacturer;
                var model = device.Model;
                var brand = ModemHelper.DetectBrand(manufacturer, model);

                _log.LogInformation("{Ip}: HiLink OK | IMEI={IMEI} IMSI={IMSI} Brand={Brand} Model={Model}",
                    device.Ip, imei, imsi, brand, model);

                await _db.EnqueueAsync(new()
                {
                    Type = DatabaseWriteChannel.Op.InsertModem,
                    Data = new { IMEI = imei, IMSI = imsi, ComPort = (string?)null, Manufacturer = manufacturer, Model = model, Brand = (int)brand }
                });

                using var devScope = _services.CreateScope();
                var db = devScope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
                db.ChangeTracker.Clear();
                Modem? modem = null;
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(200, ct);
                    modem = await db.Modems.FirstOrDefaultAsync(m => m.IMEI == imei, ct);
                    if (modem != null) break;
                }

                if (modem == null)
                {
                    _log.LogWarning("{Ip}: Modem not found after insert — freeing IMEI {IMEI}", device.Ip, imei);
                    _activeImeis.TryRemove(imei, out _);
                    try { hilink.Dispose(); } catch { }
                    return;
                }

                var handler = new ModemHandler(hilink, _db, _loggerFactory.CreateLogger<ModemHandler>(), _config, modem.Id, device.Ip, isHiLink: true, meetMob: _meetMob);

                _handlers[device.Ip] = (handler, imei);
                startedNewHandlers = true;
                _log.LogInformation("{Ip}: Handler started (total active: {Count})", device.Ip, _handlers.Count);

                var capturedModemId = modem.Id;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!await handler.StartAsync(ct))
                        {
                            _activeImeis.TryRemove(imei, out _);
                            _handlers.TryRemove(device.Ip, out _);
                            _noSimIps.Add(device.Ip);
                            _log.LogWarning("{Ip}: Handler StartAsync returned false (no SIM) — will skip in future scans", device.Ip);
                            try { handler.Dispose(); } catch { }
                            try { await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = capturedModemId, Status = ModemStatus.Offline } }); } catch { }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _activeImeis.TryRemove(imei, out _);
                        _handlers.TryRemove(device.Ip, out _);
                        _log.LogWarning(ex, "{Ip}: Handler failed — setting modem Offline", device.Ip);
                        try { handler.Dispose(); } catch { }
                        try { await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = capturedModemId, Status = ModemStatus.Offline } }); } catch { }
                    }
                }, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "{Ip}: HiLink probe failed", device.Ip);
            }
        });

        await Task.WhenAll(processTasks);

        if (ct.IsCancellationRequested) return;

        if (startedNewHandlers)
        {
            _log.LogDebug("Skipping orphan check — new handlers starting this cycle");
            return;
        }

        var activeImeiArray = _activeImeis.Keys.ToArray();
        try
        {
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateOrphanedModems, Data = new { ActiveImeis = activeImeiArray } });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to enqueue orphan check");
        }
    }

}
