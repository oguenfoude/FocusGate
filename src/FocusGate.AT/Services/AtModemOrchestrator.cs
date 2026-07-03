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

namespace FocusGate.AT.Services;

public class AtModemOrchestrator : BackgroundService
{
    private const int MaxModems = 15;
    private readonly IServiceProvider _services;
    private readonly DatabaseWriteChannel _db;
    private readonly ILogger<AtModemOrchestrator> _log;
    private readonly IConfigProvider _config;
    private readonly ConcurrentDictionary<string, (ModemHandler handler, string imei)> _handlers = new();
    private readonly ConcurrentDictionary<string, byte> _activeImeis = new();
    private readonly ConcurrentDictionary<string, byte> _failedPorts = new();

    public AtModemOrchestrator(IServiceProvider services, DatabaseWriteChannel db,
        ILogger<AtModemOrchestrator> log, IConfigProvider config)
    {
        _services = services;
        _db = db;
        _log = log;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var enabled = _config.Get("at.enabled", "true");
        if (!enabled.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation("AT scanning disabled by config");
            return;
        }

        _log.LogInformation("AT Orchestrator started (max {Max} modems)", MaxModems);

        while (!ct.IsCancellationRequested)
        {
            try { await ScanAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "Scan error"); }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("AT Orchestrator shutting down, disposing {Count} handlers...", _handlers.Count);

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

        var ports = SerialPort.GetPortNames();

        foreach (var kv in _handlers.Where(kv => !kv.Value.handler.IsAlive))
        {
            var (handler, imei) = kv.Value;
            _log.LogWarning("{Port}: Dead handler, freeing IMEI {IMEI}", kv.Key, imei);
            _handlers.TryRemove(kv.Key, out _);
            _activeImeis.TryRemove(imei, out _);
            try { handler.Dispose(); } catch { }
        }

        bool startedNewHandlers = false;

        var currentPorts = new HashSet<string>(ports);
        foreach (var key in _failedPorts.Keys.Where(k => !currentPorts.Contains(k)).ToList())
            _failedPorts.TryRemove(key, out _);

        var toProbe = ports.Where(p => !_handlers.ContainsKey(p) && !_failedPorts.ContainsKey(p) && _handlers.Count < MaxModems).ToList();
        if (toProbe.Count == 0) return;

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
                try { handler.Dispose(); } catch { }
                continue;
            }

            _handlers[port] = (handler, imei);
            startedNewHandlers = true;
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
        catch { }
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

            var brand = ModemHelper.DetectBrand(manufacturer, model);

            _log.LogDebug("{Port}: IMEI={IMEI} IMSI={IMSI} Brand={Brand} Mfg={Mfg} Model={Model}",
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

}
