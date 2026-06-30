using System.IO;
using FocusGate.Core.DTOs;
using FocusGate.Core.Enums;
using FocusGate.Core.Interfaces;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

public class ModemHandler : IDisposable
{
    private readonly IAtCommandService _at;
    private readonly DatabaseWriteChannel _db;
    private readonly ILogger<ModemHandler> _log;
    private readonly IConfigProvider _config;
    private readonly int _modemId;
    private readonly string _comPort;
    private readonly bool _isHiLink;
    private long _simCardId;
    private bool _disposed;
    private CancellationTokenSource _loopCts;
    private Task? _watchdogLoop;
    private Task? _pollLoop;
    private Task? _networkRetryLoop;
    private readonly SemaphoreSlim _atLock = new(1, 1);
    private DateTime? _ussdUnavailableSince;

    public bool IsAlive => _at?.IsOpen == true;

    public ModemHandler(IAtCommandService at, DatabaseWriteChannel db,
        ILogger<ModemHandler> log, IConfigProvider config, int modemId, string comPort, bool isHiLink = false)
    {
        _at = at;
        _db = db;
        _log = log;
        _config = config;
        _modemId = modemId;
        _comPort = comPort;
        _isHiLink = isHiLink;
        _loopCts = new CancellationTokenSource();
    }

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        try
        {
            _log.LogInformation("Modem {Id}: {Port} starting ({Type})...", _modemId, _comPort, _isHiLink ? "HiLink" : "AT");

            var imei = await _at.GetImeiAsync();
            if (string.IsNullOrEmpty(imei)) { _log.LogWarning("Modem {Id}: No IMEI", _modemId); return false; }
            _log.LogInformation("Modem {Id}: IMEI={IMEI}", _modemId, imei);

            if (!_isHiLink)
            {
                var pinResp = await _at.SendCommandAsync("AT+CPIN?");
                _log.LogDebug("Modem {Id}: CPIN? -> {Resp}", _modemId, pinResp.ReplaceLineEndings(" "));
                if (pinResp.Contains("SIM PIN") || pinResp.Contains("SIM PUK"))
                {
                    _log.LogWarning("Modem {Id}: SIM is PIN/PUK locked, cannot proceed", _modemId);
                    return false;
                }

                var manufacturer = await _at.SendCommandAsync("AT+CGMI");
                _log.LogDebug("Modem {Id}: Manufacturer -> {Resp}", _modemId, manufacturer.ReplaceLineEndings(" "));

                var model = await _at.SendCommandAsync("AT+CGMM");
                _log.LogDebug("Modem {Id}: Model -> {Resp}", _modemId, model.ReplaceLineEndings(" "));

                var zteResp = await _at.SendCommandAsync("AT+ZCDRUN=2");
                if (!zteResp.Contains("ERROR"))
                    _log.LogInformation("Modem {Id}: ZTE modem mode forced (AT+ZCDRUN=2)", _modemId);

                var huaweiResp = await _at.SendCommandAsync("AT^U2DIAG=0");
                if (!huaweiResp.Contains("ERROR"))
                    _log.LogInformation("Modem {Id}: Huawei modem mode forced (AT^U2DIAG=0)", _modemId);
            }

            var imsi = await _at.GetImsiAsync();
            if (string.IsNullOrEmpty(imsi)) { _log.LogWarning("Modem {Id}: No SIM", _modemId); return false; }
            _log.LogInformation("Modem {Id}: IMSI={IMSI}", _modemId, imsi);

            NetworkRegistration netReg = NetworkRegistration.Unknown;
            for (int i = 1; i <= 10; i++)
            {
                netReg = await _at.GetNetworkRegistrationAsync();
                _log.LogInformation("Modem {Id}: Network {Attempt}/10 - {Status}", _modemId, i, netReg);
                if (netReg == NetworkRegistration.Registered) break;
                await Task.Delay(5000, ct);
            }

            _log.LogDebug("Modem {Id}: Waiting 5s for network...", _modemId);
            await Task.Delay(5000, ct);

            if (!_isHiLink)
            {
                var csqResp = await _at.SendCommandAsync("AT+CSQ");
                _log.LogDebug("Modem {Id}: Signal -> {Resp}", _modemId, csqResp.ReplaceLineEndings(" "));

                var cmgf = await _at.SendCommandAsync("AT+CMGF=1");
                _log.LogDebug("Modem {Id}: CMGF -> {Resp}", _modemId, cmgf.ReplaceLineEndings(" "));
                var charset = await _at.TrySetCharsetAsync("IRA");
                if (!charset)
                {
                    _log.LogDebug("Modem {Id}: IRA not supported, trying GSM...", _modemId);
                    charset = await _at.TrySetCharsetAsync("GSM");
                    if (!charset)
                    {
                        _log.LogDebug("Modem {Id}: GSM not supported, trying UCS2...", _modemId);
                        charset = await _at.TrySetCharsetAsync("UCS2");
                    }
                }
                _log.LogDebug("Modem {Id}: CSCS={Result}", _modemId, charset);
            }

            var (existingImsi, existingPhone) = await _db.GetActiveSimInfoAsync(_modemId);

            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpsertSimCard, Data = new { ModemId = _modemId, IMSI = imsi, PhoneNumber = existingPhone } });

            _simCardId = await ResolveSimCardIdAsync();

            if (!_isHiLink)
            {
                var cpms = await _at.SendCommandAsync("AT+CPMS?");
                _log.LogDebug("Modem {Id}: CPMS? -> {Resp}", _modemId, cpms.ReplaceLineEndings(" "));
                cpms = await _at.SendCommandAsync("AT+CPMS=\"SM\",\"SM\",\"SM\"");
                _log.LogDebug("Modem {Id}: CPMS=SM -> {Resp}", _modemId, cpms.ReplaceLineEndings(" "));
                var cnmi = await _at.SendCommandAsync("AT+CNMI=2,1,0,0,0");
                _log.LogDebug("Modem {Id}: CNMI -> {Resp}", _modemId, cnmi.ReplaceLineEndings(" "));
            }

            var messages = await _at.ReadAllSmsAsync();
            _log.LogDebug("Modem {Id}: {Count} SMS on SIM", _modemId, messages.Count);
            var startupBalanceTrigger = false;
            if (messages.Count > 0)
            {
                var tcsList = new List<Task<bool>>();
                foreach (var msg in messages)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    tcsList.Add(tcs.Task);
                    await _db.EnqueueAsync(new()
                    {
                        Type = DatabaseWriteChannel.Op.InsertSms,
                        Data = new SmsRecord
                        {
                            SimCardId = _simCardId,
                            SenderNumber = msg.Sender,
                            Content = msg.Content,
                            ReceivedAt = msg.ReceivedAt
                        },
                        Completed = tcs
                    });
                    if (IsMobilisBalanceTrigger(msg))
                        startupBalanceTrigger = true;
                }
                var results = await Task.WhenAll(tcsList);
                var savedCount = results.Count(r => r);
                var skippedCount = results.Length - savedCount;
                await _at.DeleteAllSmsAsync();
                _log.LogInformation("Modem {Id}: Processed {TotalCount} startup SMS ({SavedCount} saved, {SkippedCount} skipped/duplicates) and deleted from SIM",
                    _modemId, messages.Count, savedCount, skippedCount);
            }

            var status = netReg == NetworkRegistration.Registered ? ModemStatus.Online : ModemStatus.PendingNetwork;
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = _modemId, Status = status } });

            var loopToken = _loopCts.Token;
            _watchdogLoop = WatchdogLoopAsync(
                TimeSpan.FromSeconds(_config.Get<int>("modem.watchdog.interval", 30)), loopToken);
            _pollLoop = PollSmsLoopAsync(
                TimeSpan.FromSeconds(_config.Get<int>("modem.sms.poll.interval", 30)), loopToken);

            if (status == ModemStatus.Online)
            {
                _ = Task.Run(async () =>
                {
                    if (startupBalanceTrigger)
                        await RunBalanceCheckFromSmsAsync(loopToken);
                    else
                        await TryGetPhoneAndBalanceAsync(loopToken);
                }, loopToken);
            }

            _networkRetryLoop = NetworkRetryLoopAsync(loopToken);

            _log.LogInformation("Modem {Id}: {Status} on {Port}", _modemId, status, _comPort);
            return true;
        }
        catch (IOException) { await DisconnectAsync(); return false; }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not open")) { await DisconnectAsync(); return false; }
        catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Failed", _modemId); return false; }
    }

    private async Task WatchdogLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }

            if (_disposed) break;
            try
            {
                await _atLock.WaitAsync(ct);
                try { await WatchdogAsync(); }
                finally { _atLock.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Watchdog loop error", _modemId); }
        }
    }

    private async Task PollSmsLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }

            if (_disposed) break;

            bool needsBalanceCheck = false;
            try
            {
                await _atLock.WaitAsync(ct);
                try { needsBalanceCheck = await PollSmsAsync(); }
                finally { _atLock.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Poll loop error", _modemId); }

            if (needsBalanceCheck && !_disposed)
            {
                try
                {
                    await _atLock.WaitAsync(ct);
                    try { await RunBalanceCheckFromSmsAsync(ct); }
                    finally { _atLock.Release(); }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Modem {Id}: SMS-triggered balance check error", _modemId); }
            }
        }
    }

    private async Task RunBalanceCheckFromSmsAsync(CancellationToken ct)
    {
        _log.LogInformation("Modem {Id}: Recharge/transfer SMS detected — running *222# to confirm real balance...", _modemId);
        var balance = await _at.GetBalanceAsync();
        if (balance.HasValue)
        {
            _log.LogInformation("Modem {Id}: Balance confirmed: {Balance:F2} DZD", _modemId, balance.Value);
            await _db.EnqueueAsync(new()
            {
                Type = DatabaseWriteChannel.Op.UpdateSimBalanceFromSms,
                Data = new { ModemId = _modemId, Balance = balance.Value }
            });
        }
        else
        {
            _log.LogWarning("Modem {Id}: *222# returned no balance after recharge/transfer SMS", _modemId);
            _ussdUnavailableSince = DateTime.UtcNow;
        }
    }

    private async Task<long> ResolveSimCardIdAsync()
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            var id = await _db.GetActiveSimCardIdAsync(_modemId);
            if (id > 0) return id;
        }
        _log.LogWarning("Modem {Id}: Could not resolve SimCardId after 15s", _modemId);
        return 0;
    }

    private async Task DisconnectAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _loopCts.Cancel();
        if (_watchdogLoop != null) try { await _watchdogLoop; } catch { }
        if (_pollLoop != null) try { await _pollLoop; } catch { }
        if (_networkRetryLoop != null) try { await _networkRetryLoop; } catch { }
        try
        {
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = _modemId, Status = ModemStatus.Offline } });
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemComPort, Data = new { ModemId = _modemId, ComPort = (string?)null } });
        }
        catch { }
        _at.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loopCts.Cancel();
        try
        {
            var task = Task.Run(async () =>
            {
                if (_watchdogLoop != null) try { await _watchdogLoop; } catch { }
                if (_pollLoop != null) try { await _pollLoop; } catch { }
                if (_networkRetryLoop != null) try { await _networkRetryLoop; } catch { }
                try
                {
                    await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = _modemId, Status = ModemStatus.Offline } });
                    await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemComPort, Data = new { ModemId = _modemId, ComPort = (string?)null } });
                }
                catch { }
            });
            task.Wait(TimeSpan.FromSeconds(3));
        }
        catch { }
        _loopCts.Dispose();
        _atLock.Dispose();
        _at?.Dispose();
    }

    private async Task WatchdogAsync()
    {
        if (_at == null || !_at.IsOpen) { return; }

        if (_isHiLink)
        {
            var alive = await _at.IsAliveAsync();
            if (!alive)
            {
                _log.LogWarning("Modem {Id}: HiLink unreachable, disconnecting for re-probe", _modemId);
                await DisconnectAsync();
            }
            else
            {
                await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = _modemId, Status = ModemStatus.Online } });
            }
            return;
        }

        try
        {
            var resp = await _at.SendCommandAsync("AT");
            if (!resp.Contains("OK"))
            {
                _log.LogWarning("Modem {Id}: Watchdog AT failed -> disconnecting for re-probe", _modemId);
                await DisconnectAsync();
            }
            else
            {
                await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = _modemId, Status = ModemStatus.Online } });
            }
        }
        catch (IOException) { await DisconnectAsync(); }
        catch (InvalidOperationException) { await DisconnectAsync(); }
        catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Watchdog error -> disconnecting", _modemId); await DisconnectAsync(); }
    }

    private async Task<bool> PollSmsAsync()
    {
        if (_at == null || !_at.IsOpen) return false;
        var balanceTriggerNeeded = false;
        try
        {
            var messages = await _at.ReadAllSmsAsync();
            var count = messages.Count;

            if (count <= 0) return false;

            var savedCount = 0;
            var skippedCount = 0;
            foreach (var msg in messages)
            {
                try
                {
                    var tcs = new TaskCompletionSource<bool>();
                    await _db.EnqueueAsync(new()
                    {
                        Type = DatabaseWriteChannel.Op.InsertSms,
                        Data = new SmsRecord
                        {
                            SimCardId = _simCardId,
                            SenderNumber = msg.Sender,
                            Content = msg.Content,
                            ReceivedAt = msg.ReceivedAt
                        },
                        Completed = tcs
                    });
                    var wasSaved = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (wasSaved)
                    {
                        savedCount++;
                        if (!balanceTriggerNeeded && IsMobilisBalanceTrigger(msg))
                            balanceTriggerNeeded = true;
                    }
                    else
                        skippedCount++;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Modem {Id}: Failed to process SMS from {Sender}", _modemId, msg.Sender);
                }
            }

            if (savedCount > 0 || skippedCount > 0)
            {
                _log.LogInformation("Modem {Id}: Poll - {SavedCount} SMS saved, {SkippedCount} skipped", _modemId, savedCount, skippedCount);
                await _at.DeleteAllSmsAsync();
            }
        }
        catch (IOException) { await DisconnectAsync(); }
        catch (InvalidOperationException) { await DisconnectAsync(); }
        catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Poll error", _modemId); }
        return balanceTriggerNeeded;
    }

    private static bool IsMobilisBalanceTrigger(RawSmsMessage msg)
    {
        if (msg.Sender != "Mobilis" && msg.Sender != "77111") return false;
        return msg.Content.Contains("recharg", StringComparison.OrdinalIgnoreCase)
            || msg.Content.Contains("montant de", StringComparison.OrdinalIgnoreCase);
    }

    private async Task TryGetPhoneAndBalanceAsync(CancellationToken ct)
    {
        if (_ussdUnavailableSince.HasValue && DateTime.UtcNow - _ussdUnavailableSince.Value < TimeSpan.FromMinutes(10))
            return;
        _ussdUnavailableSince = null;
        try
        {
            await _atLock.WaitAsync(ct);
            try
            {
                var existingPhone = (await _db.GetActiveSimInfoAsync(_modemId)).PhoneNumber;
                if (existingPhone == 0)
                {
                    _log.LogDebug("Modem {Id}: Running USSD *101# for phone number...", _modemId);
                    var phone = await _at.GetPhoneNumberViaUssdAsync();
                    if (!string.IsNullOrEmpty(phone))
                    {
                        _log.LogInformation("Modem {Id}: Phone number: {Phone}", _modemId, phone);
                        await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateSimCardPhone, Data = new { ModemId = _modemId, PhoneNumber = long.Parse(phone) } });
                    }
                    else
                    {
                        _log.LogWarning("Modem {Id}: Phone USSD returned empty", _modemId);
                    }
                }

                _log.LogDebug("Modem {Id}: Running USSD *222# for balance...", _modemId);
                var balance = await _at.GetBalanceAsync();
                if (balance.HasValue)
                {
                    _log.LogInformation("Modem {Id}: Balance: {Balance:F2} DZD", _modemId, balance.Value);
                    await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateSimBalance, Data = new { ModemId = _modemId, Balance = balance.Value } });
                }
                else
                {
                    _log.LogWarning("Modem {Id}: Balance USSD returned empty", _modemId);
                    _ussdUnavailableSince = DateTime.UtcNow;
                    _log.LogWarning("Modem {Id}: USSD temporarily unavailable — retry in 10 min", _modemId);
                }
            }
            finally { _atLock.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Modem {Id}: USSD phone/balance error", _modemId); }
    }

    private async Task NetworkRetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), ct); }
            catch (OperationCanceledException) { break; }

            if (_disposed) break;

            try
            {
                await _atLock.WaitAsync(ct);
                try
                {
                    var netReg = await _at.GetNetworkRegistrationAsync();
                    if (netReg != NetworkRegistration.Registered)
                        continue;

                    await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = _modemId, Status = ModemStatus.Online } });

                    await TryGetPhoneAndBalanceAsync(ct);
                }
                finally { _atLock.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Network retry loop error", _modemId); }
        }
    }
}
