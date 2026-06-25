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
    private readonly Dictionary<string, (ModemHandler handler, string imei)> _handlers = new();
    private readonly HashSet<string> _activeImeis = new();
    private readonly HashSet<string> _failedPorts = new();
    private bool _huaweiChecked = false;

    public ModemOrchestrator(IServiceProvider services, DatabaseWriteChannel db, ILogger<ModemOrchestrator> log)
    {
        _services = services;
        _db = db;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Orchestrator started (max {Max} modems)", MaxModems);

        while (!ct.IsCancellationRequested)
        {
            try { await ScanAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "Scan error"); }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("Orchestrator shutting down, disposing {Count} handlers...", _handlers.Count);

        List<ModemHandler> handlers;
        lock (_handlers)
        {
            handlers = _handlers.Values.Select(v => v.handler).ToList();
            _handlers.Clear();
            _activeImeis.Clear();
        }

        foreach (var handler in handlers)
        {
            try { handler.Dispose(); }
            catch (Exception ex) { _log.LogWarning(ex, "Error disposing handler"); }
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        if (!_huaweiChecked)
        {
            _huaweiChecked = true;
            try { await HuaweiHiLinkSwitcher.DetectAndOpenBrowsersAsync(_log, ct); }
            catch (Exception ex) { _log.LogDebug("Huawei detection failed: {Error}", ex.Message); }
        }

        var ports = SerialPort.GetPortNames();

        lock (_handlers)
        {
            foreach (var port in _handlers.Where(kv => !kv.Value.handler.IsAlive).Select(kv => kv.Key).ToList())
            {
                var (handler, imei) = _handlers[port];
                _log.LogWarning("{Port}: Dead handler, freeing IMEI {IMEI}", port, imei);
                _handlers.Remove(port);
                _activeImeis.Remove(imei);
                try { handler.Dispose(); } catch { }
            }
        }

        List<string> toProbe;
        lock (_handlers)
        {
            var currentPorts = new HashSet<string>(ports);
            _failedPorts.IntersectWith(currentPorts);
            toProbe = ports.Where(p => !_handlers.ContainsKey(p) && !_failedPorts.Contains(p) && _handlers.Count < MaxModems).ToList();
        }
        if (toProbe.Count == 0) return;

        _log.LogInformation("Probing {Count} port(s) in parallel...", toProbe.Count);

        var probes = toProbe.Select(p => (Port: p, Task: ProbeAsync(p, ct))).ToList();
        var probeTimeout = Task.Delay(8000, ct);
        await Task.WhenAny(Task.WhenAll(probes.Select(x => x.Task)), probeTimeout);

        foreach (var (port, task) in probes)
        {
            lock (_handlers)
            {
                if (_handlers.Count >= MaxModems) break;
            }
            if (!task.IsCompletedSuccessfully) continue;

            var (handler, imei) = task.Result;
            if (handler == null || string.IsNullOrEmpty(imei))
            {
                lock (_handlers) { _failedPorts.Add(port); }
                continue;
            }

            lock (_activeImeis)
            {
                if (_activeImeis.Contains(imei))
                {
                    _log.LogInformation("{Port}: Duplicate IMEI {IMEI}", port, imei);
                    try { handler.Dispose(); } catch { }
                    continue;
                }
                _activeImeis.Add(imei);
            }

            lock (_handlers)
            {
                _handlers[port] = (handler, imei);
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await handler.StartAsync(ct))
                    {
                        lock (_activeImeis) { _activeImeis.Remove(imei); }
                        lock (_handlers) { _handlers.Remove(port); }
                        try { handler.Dispose(); } catch { }
                    }
                }
                catch
                {
                    lock (_activeImeis) { _activeImeis.Remove(imei); }
                    lock (_handlers) { _handlers.Remove(port); }
                    try { handler.Dispose(); } catch { }
                }
            }, ct);
        }

        // Orphan check runs AFTER probes so all active IMEIs are registered
        string[] activeImeiArray;
        lock (_activeImeis)
        {
            activeImeiArray = _activeImeis.ToArray();
        }
        await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateOrphanedModems, Data = new { ActiveImeis = activeImeiArray } });
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
        if (mfg.Contains("mediaTek") || mdl.Contains("mtk")) return ModemBrand.MediaTek;

        if (!string.IsNullOrEmpty(mfg)) return ModemBrand.Other;
        return ModemBrand.Unknown;
    }
}
