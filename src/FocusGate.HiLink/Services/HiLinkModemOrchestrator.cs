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
    private readonly ConcurrentDictionary<string, (ModemHandler handler, string imei)> _handlers = new();
    private readonly ConcurrentDictionary<string, byte> _activeImeis = new();
    private readonly ConcurrentDictionary<string, int> _blacklistedIps = new();
    private readonly HashSet<string> _knownModemIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noSimIps = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxIpFailures = 3;

    public HiLinkModemOrchestrator(IServiceProvider services, DatabaseWriteChannel db,
        ILogger<HiLinkModemOrchestrator> log, IConfigProvider config)
    {
        _services = services;
        _db = db;
        _log = log;
        _config = config;
        _maxModems = _config.Get("modem.max_count", 15);
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

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _log.LogInformation("Scan cycle starting ({Count} handlers active, {Blacklisted} blacklisted IPs)", _handlers.Count, _blacklistedIps.Count);
                await ScanAsync(ct);
                _log.LogInformation("Scan cycle complete ({Count} modems online, {Blacklisted} blacklisted IPs)", _handlers.Count, _blacklistedIps.Count);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Scan cycle error"); }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("HiLink Orchestrator stopped");
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

        var now = DateTime.UtcNow;
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

        foreach (var device in devices)
        {
            if (_handlers.Count >= _maxModems) break;

            if (!string.IsNullOrEmpty(device.Imei) && _activeImeis.ContainsKey(device.Imei))
            {
                continue;
            }

            try
            {
                var config = scope.ServiceProvider.GetRequiredService<IConfigProvider>();
                var hilinkLog = scope.ServiceProvider.GetRequiredService<ILogger<HiLinkCommandService>>();
                var hilink = new HiLinkCommandService(hilinkLog, config);

                await hilink.OpenAsync(device.Ip);

                if (!await hilink.IsAliveAsync())
                {
                    _log.LogWarning("{Ip}: Alive check failed", device.Ip);
                    try { hilink.Dispose(); } catch { }
                    continue;
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
                    continue;
                }

                if (!_activeImeis.TryAdd(imei, 0))
                {
                    try { hilink.Dispose(); } catch { }
                    continue;
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

                var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
                Modem? modem = null;
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(200, ct);
                    modem = await db.Modems.FirstOrDefaultAsync(m => m.IMEI == imei, ct);
                    if (modem != null) break;
                }

                if (modem == null) { _log.LogWarning("{Ip}: Modem not found after insert — freeing IMEI {IMEI}", device.Ip, imei); _activeImeis.TryRemove(imei, out _); try { hilink.Dispose(); } catch { } continue; }

                var handlerLog = scope.ServiceProvider.GetRequiredService<ILogger<ModemHandler>>();
                var writeChannel = scope.ServiceProvider.GetRequiredService<DatabaseWriteChannel>();
                var handler = new ModemHandler(hilink, writeChannel, handlerLog, config, modem.Id, device.Ip, isHiLink: true);

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
        }

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
