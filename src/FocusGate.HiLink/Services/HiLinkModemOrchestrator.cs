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
    private const int MaxModems = 10;
    private readonly IServiceProvider _services;
    private readonly DatabaseWriteChannel _db;
    private readonly ILogger<HiLinkModemOrchestrator> _log;
    private readonly IConfigProvider _config;
    private readonly ConcurrentDictionary<string, (ModemHandler handler, string imei)> _handlers = new();
    private readonly ConcurrentDictionary<string, byte> _activeImeis = new();
    private int _cycleCount;

    public HiLinkModemOrchestrator(IServiceProvider services, DatabaseWriteChannel db,
        ILogger<HiLinkModemOrchestrator> log, IConfigProvider config)
    {
        _services = services;
        _db = db;
        _log = log;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var enabled = _config.Get("hilink.enabled", "true");
        if (!enabled.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("HiLink scanning DISABLED by config (hilink.enabled=false)");
            return;
        }

        _log.LogInformation("HiLink Orchestrator ready (max {Max} modems)", MaxModems);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _cycleCount++;
                _log.LogInformation("--- Scan cycle #{Cycle} ---", _cycleCount);
                await ScanAsync(ct);
                _log.LogInformation("Active handlers: {Count}", _handlers.Count);
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
        if (_handlers.Count >= MaxModems)
        {
            _log.LogDebug("Max modems reached ({Max}), skipping scan", MaxModems);
            return;
        }

        foreach (var kv in _handlers.Where(kv => !kv.Value.handler.IsAlive))
        {
            var (handler, imei) = kv.Value;
            _log.LogWarning("{Ip}: Dead handler, freeing IMEI {IMEI}", kv.Key, imei);
            _handlers.TryRemove(kv.Key, out _);
            _activeImeis.TryRemove(imei, out _);
            try { handler.Dispose(); } catch { }
        }

        var ipsRaw = _config.Get("hilink.scan_ips", "");
        string[] toScan;

        if (!string.IsNullOrWhiteSpace(ipsRaw))
        {
            toScan = ipsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(ip => !_handlers.ContainsKey(ip))
                .ToArray();
            _log.LogInformation("Using config IPs: {Ips}", string.Join(", ", toScan));
        }
        else
        {
            var autoIps = HiLinkDiscovery.DiscoverGatewayIps();
            toScan = autoIps.Where(ip => !_handlers.ContainsKey(ip)).ToArray();
            _log.LogInformation("Auto-detected {Count} gateway IPs: {Ips}", toScan.Length, string.Join(", ", toScan));
        }

        if (toScan.Length == 0)
        {
            _log.LogWarning("No IPs to scan");
            return;
        }

        using var scope = _services.CreateScope();
        var discoveryLog = scope.ServiceProvider.GetRequiredService<ILogger<HiLinkDiscovery>>();
        var discovery = new HiLinkDiscovery(discoveryLog);
        var probeTimeout = int.TryParse(_config.Get("hilink.probe_timeout_ms", "3000"), out var t) ? t : 3000;

        _log.LogInformation("Probing {Count} IPs (timeout {Ms}ms each)...", toScan.Length, probeTimeout);
        var devices = await discovery.DiscoverAsync(toScan, probeTimeout);
        _log.LogInformation("Found {Count} HiLink device(s)", devices.Count);

        foreach (var device in devices)
        {
            if (_handlers.Count >= MaxModems) break;

            if (!string.IsNullOrEmpty(device.Imei) && _activeImeis.ContainsKey(device.Imei))
            {
                _log.LogInformation("{Ip}: Duplicate IMEI {IMEI}, skipping", device.Ip, device.Imei);
                continue;
            }

            try
            {
                var config = scope.ServiceProvider.GetRequiredService<IConfigProvider>();
                var hilinkLog = scope.ServiceProvider.GetRequiredService<ILogger<HiLinkCommandService>>();
                var hilink = new HiLinkCommandService(hilinkLog, config);

                _log.LogInformation("{Ip}: Connecting...", device.Ip);
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
                    _log.LogWarning("{Ip}: No IMEI", device.Ip);
                    try { hilink.Dispose(); } catch { }
                    continue;
                }

                if (!_activeImeis.TryAdd(imei, 0))
                {
                    _log.LogInformation("{Ip}: Duplicate IMEI {IMEI}", device.Ip, imei);
                    try { hilink.Dispose(); } catch { }
                    continue;
                }

                var imsi = await hilink.GetImsiAsync();
                var manufacturer = device.Manufacturer;
                var model = device.Model;
                var brand = DetectBrand(manufacturer, model);

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

                if (modem == null) { _log.LogWarning("{Ip}: Modem not found after insert", device.Ip); try { hilink.Dispose(); } catch { } continue; }

                var handlerLog = scope.ServiceProvider.GetRequiredService<ILogger<ModemHandler>>();
                var writeChannel = scope.ServiceProvider.GetRequiredService<DatabaseWriteChannel>();
                var handler = new ModemHandler(hilink, writeChannel, handlerLog, config, modem.Id, device.Ip, isHiLink: true);

                _handlers[device.Ip] = (handler, imei);
                _log.LogInformation("{Ip}: Handler started (total active: {Count})", device.Ip, _handlers.Count);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!await handler.StartAsync(ct))
                        {
                            _activeImeis.TryRemove(imei, out _);
                            _handlers.TryRemove(device.Ip, out _);
                            _log.LogWarning("{Ip}: Handler StartAsync returned false", device.Ip);
                            try { handler.Dispose(); } catch { }
                        }
                    }
                    catch
                    {
                        _activeImeis.TryRemove(imei, out _);
                        _handlers.TryRemove(device.Ip, out _);
                        try { handler.Dispose(); } catch { }
                    }
                }, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "{Ip}: HiLink probe failed", device.Ip);
            }
        }

        if (ct.IsCancellationRequested) return;

        var activeImeiArray = _activeImeis.Keys.ToArray();
        try
        {
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateOrphanedModems, Data = new { ActiveImeis = activeImeiArray } });
        }
        catch { }
    }

    private static ModemBrand DetectBrand(string manufacturer, string model)
    {
        var mfg = (manufacturer ?? "").ToLowerInvariant();
        var mdl = (model ?? "").ToLowerInvariant();

        if (mfg.Contains("zte") || mdl.Contains("zte")) return ModemBrand.ZTE;
        if (mfg.Contains("huawei") || mdl.Contains("huawei")) return ModemBrand.Huawei;
        if (mfg.Contains("quectel") || mdl.Contains("quectel")) return ModemBrand.Quectel;
        if (mfg.Contains("simcom") || mdl.Contains("simcom")) return ModemBrand.SIMCom;
        if (mfg.Contains("sierra") || mdl.Contains("sierra")) return ModemBrand.SierraWireless;
        if (mfg.Contains("ericsson") || mdl.Contains("ericsson")) return ModemBrand.Ericsson;
        if (mfg.Contains("mediatek") || mdl.Contains("mtk")) return ModemBrand.MediaTek;

        if (!string.IsNullOrEmpty(mfg)) return ModemBrand.Other;
        return ModemBrand.Unknown;
    }
}
