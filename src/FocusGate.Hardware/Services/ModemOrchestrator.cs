using System.Collections.Concurrent;
using System.IO.Ports;
using FocusGate.Core.Enums;
using FocusGate.Core.Interfaces;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using FocusGate.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FocusGate.Hardware.Services;

public class ModemOrchestrator : BackgroundService
{
    private const int MaxModems = 10;
    private readonly IServiceProvider _services;
    private readonly DatabaseWriteChannel _db;
    private readonly ILogger<ModemOrchestrator> _log;
    private readonly IConfigProvider _config;
    private readonly ConcurrentDictionary<string, (ModemHandler handler, string imei)> _handlers = new();
    private readonly ConcurrentDictionary<string, byte> _activeImeis = new();
    private readonly ConcurrentDictionary<string, byte> _failedPorts = new();
    private readonly ConcurrentDictionary<string, byte> _activeHiLinkIps = new();
    private int _cycleCount;

    public ModemOrchestrator(IServiceProvider services, DatabaseWriteChannel db,
        ILogger<ModemOrchestrator> log, IConfigProvider config)
    {
        _services = services;
        _db = db;
        _log = log;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Orchestrator started (max {Max} modems)", MaxModems);

        while (!ct.IsCancellationRequested)
        {
            try { await ScanAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "Scan error"); }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            _cycleCount++;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("Orchestrator shutting down, disposing {Count} handlers...", _handlers.Count);

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
        if (_handlers.Count >= MaxModems) return;

        await ScanComPortsAsync(ct);

        if (_handlers.Count < MaxModems && _cycleCount % 2 == 0)
            await ScanHiLinkAsync(ct);

        if (ct.IsCancellationRequested) return;

        var activeImeiArray = _activeImeis.Keys.ToArray();
        try
        {
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateOrphanedModems, Data = new { ActiveImeis = activeImeiArray } });
        }
        catch { }
    }

    private async Task ScanComPortsAsync(CancellationToken ct)
    {
        var ports = SerialPort.GetPortNames();

        foreach (var kv in _handlers.Where(kv => !kv.Value.handler.IsAlive && !_activeHiLinkIps.ContainsKey(kv.Key)))
        {
            var (handler, imei) = kv.Value;
            _log.LogWarning("{Port}: Dead handler, freeing IMEI {IMEI}", kv.Key, imei);
            _handlers.TryRemove(kv.Key, out _);
            _activeImeis.TryRemove(imei, out _);
            try { handler.Dispose(); } catch { }
        }

        var currentPorts = new HashSet<string>(ports);
        foreach (var key in _failedPorts.Keys.Where(k => !currentPorts.Contains(k)).ToList())
            _failedPorts.TryRemove(key, out _);

        var toProbe = ports.Where(p => !_handlers.ContainsKey(p) && !_failedPorts.ContainsKey(p) && _handlers.Count < MaxModems).ToList();
        if (toProbe.Count == 0) return;

        _log.LogInformation("Probing {Count} port(s) in parallel...", toProbe.Count);

        var probes = toProbe.Select(p => (Port: p, Task: ProbeAsync(p, ct))).ToList();
        var probeTimeout = Task.Delay(8000, ct);
        await Task.WhenAny(Task.WhenAll(probes.Select(x => x.Task)), probeTimeout);

        foreach (var (port, task) in probes)
        {
            if (_handlers.Count >= MaxModems) break;
            if (!task.IsCompletedSuccessfully) continue;

            var (handler, imei) = task.Result;
            if (handler == null || string.IsNullOrEmpty(imei))
            {
                _failedPorts.TryAdd(port, 0);
                continue;
            }

            if (!_activeImeis.TryAdd(imei, 0))
            {
                _log.LogInformation("{Port}: Duplicate IMEI {IMEI}", port, imei);
                try { handler.Dispose(); } catch { }
                continue;
            }

            _handlers[port] = (handler, imei);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await handler.StartAsync(ct))
                    {
                        _activeImeis.TryRemove(imei, out _);
                        _handlers.TryRemove(port, out _);
                        try { handler.Dispose(); } catch { }
                    }
                }
                catch
                {
                    _activeImeis.TryRemove(imei, out _);
                    _handlers.TryRemove(port, out _);
                    try { handler.Dispose(); } catch { }
                }
            }, ct);
        }
    }

    private async Task ScanHiLinkAsync(CancellationToken ct)
    {
        var enabled = _config.Get("hilink.enabled", "true");
        if (!enabled.Equals("true", StringComparison.OrdinalIgnoreCase)) return;

        var ipsRaw = _config.Get("hilink.scan_ips", "192.168.8.1");
        var ips = ipsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var timeoutMs = int.TryParse(_config.Get("hilink.probe_timeout_ms", "3000"), out var t) ? t : 3000;

        var toScan = ips.Where(ip => !_activeHiLinkIps.ContainsKey(ip)).ToArray();
        if (toScan.Length == 0) return;

        _log.LogInformation("Scanning {Count} network IPs for HiLink modems...", toScan.Length);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
        var discoveryLog = scope.ServiceProvider.GetRequiredService<ILogger<HiLinkDiscovery>>();
        var discovery = new HiLinkDiscovery(discoveryLog);
        var devices = await discovery.DiscoverAsync(toScan, timeoutMs);

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

                await hilink.OpenAsync(device.Ip);

                if (!await hilink.IsAliveAsync())
                {
                    _log.LogWarning("{Ip}: HiLink alive check failed", device.Ip);
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

                _log.LogInformation("{Ip}: HiLink IMEI={IMEI} IMSI={IMSI} Brand={Brand} Model={Model}",
                    device.Ip, imei, imsi, brand, model);

                await _db.EnqueueAsync(new()
                {
                    Type = DatabaseWriteChannel.Op.InsertModem,
                    Data = new { IMEI = imei, IMSI = imsi, ComPort = (string?)null, Manufacturer = manufacturer, Model = model, Brand = (int)brand }
                });

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

                _activeHiLinkIps[device.Ip] = 0;
                _handlers[device.Ip] = (handler, imei);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!await handler.StartAsync(ct))
                        {
                            _activeImeis.TryRemove(imei, out _);
                            _activeHiLinkIps.TryRemove(device.Ip, out _);
                            _handlers.TryRemove(device.Ip, out _);
                            try { handler.Dispose(); } catch { }
                        }
                    }
                    catch
                    {
                        _activeImeis.TryRemove(imei, out _);
                        _activeHiLinkIps.TryRemove(device.Ip, out _);
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
    }

    private async Task<(ModemHandler? handler, string imei)> ProbeAsync(string port, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfigProvider>();
        var atLog = scope.ServiceProvider.GetRequiredService<ILogger<AtCommandService>>();
        var at = new AtCommandService(atLog, config);

        try
        {
            await at.OpenAsync(port);
            if (!await at.IsAliveAsync()) { try { at.Dispose(); } catch { } return (null, ""); }

            var imei = await at.GetImeiAsync();
            if (string.IsNullOrEmpty(imei)) { try { at.Dispose(); } catch { } return (null, ""); }

            var imsi = await at.GetImsiAsync();
            if (string.IsNullOrEmpty(imsi)) { _log.LogWarning("{Port}: No SIM", port); try { at.Dispose(); } catch { } return (null, ""); }

            var manufacturerResp = await at.SendCommandAsync("AT+CGMI");
            var manufacturer = manufacturerResp.Replace("OK", "").ReplaceLineEndings(" ").Trim();
            var modelResp = await at.SendCommandAsync("AT+CGMM");
            var model = modelResp.Replace("OK", "").ReplaceLineEndings(" ").Trim();

            var brand = DetectBrand(manufacturer, model);

            _log.LogInformation("{Port}: IMEI={IMEI} IMSI={IMSI} Brand={Brand} Mfg={Mfg} Model={Model}",
                port, imei, imsi, brand, manufacturer, model);

            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.InsertModem, Data = new { IMEI = imei, IMSI = imsi, ComPort = port, Manufacturer = manufacturer, Model = model, Brand = (int)brand } });

            Modem? modem = null;
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(200, ct);
                modem = await db.Modems.FirstOrDefaultAsync(m => m.IMEI == imei, ct);
                if (modem != null) break;
            }

            if (modem == null) { _log.LogWarning("{Port}: Modem not found after insert", port); try { at.Dispose(); } catch { } return (null, ""); }

            var handlerLog = scope.ServiceProvider.GetRequiredService<ILogger<ModemHandler>>();
            var writeChannel = scope.ServiceProvider.GetRequiredService<DatabaseWriteChannel>();

            return (new ModemHandler(at, writeChannel, handlerLog, config, modem.Id, port), imei);
        }
        catch (Exception ex) { _log.LogDebug("{Port}: {Error}", port, ex.Message); try { at.Dispose(); } catch { } return (null, ""); }
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
