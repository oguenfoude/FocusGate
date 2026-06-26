using System.IO;
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
    private DateTime _lastUssdCheck;
    private bool _disposed;
    private CancellationTokenSource _loopCts;
    private Task? _watchdogLoop;
    private Task? _pollLoop;
    private Task? _balanceLoop;
    private readonly SemaphoreSlim _atLock = new(1, 1);

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
                _log.LogInformation("Modem {Id}: CPIN? -> {Resp}", _modemId, pinResp.ReplaceLineEndings(" "));
                if (pinResp.Contains("SIM PIN") || pinResp.Contains("SIM PUK"))
                {
                    _log.LogWarning("Modem {Id}: SIM is PIN/PUK locked, cannot proceed", _modemId);
                    return false;
                }

                var manufacturer = await _at.SendCommandAsync("AT+CGMI");
                _log.LogInformation("Modem {Id}: Manufacturer -> {Resp}", _modemId, manufacturer.ReplaceLineEndings(" "));

                var model = await _at.SendCommandAsync("AT+CGMM");
                _log.LogInformation("Modem {Id}: Model -> {Resp}", _modemId, model.ReplaceLineEndings(" "));

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
            for (int i = 1; i <= 5; i++)
            {
                netReg = await _at.GetNetworkRegistrationAsync();
                if (netReg == NetworkRegistration.Registered) break;
                _log.LogInformation("Modem {Id}: Network {Attempt}/5 - {Status}", _modemId, i, netReg);
                await Task.Delay(3000, ct);
            }

            _log.LogInformation("Modem {Id}: Waiting 5s for network...", _modemId);
            await Task.Delay(5000, ct);

            if (!_isHiLink)
            {
                var csqResp = await _at.SendCommandAsync("AT+CSQ");
                _log.LogInformation("Modem {Id}: Signal -> {Resp}", _modemId, csqResp.ReplaceLineEndings(" "));

                var cmgf = await _at.SendCommandAsync("AT+CMGF=1");
                _log.LogInformation("Modem {Id}: CMGF -> {Resp}", _modemId, cmgf.ReplaceLineEndings(" "));
                var charset = await _at.TrySetCharsetAsync("IRA");
                if (!charset)
                {
                    _log.LogInformation("Modem {Id}: IRA not supported, trying GSM...", _modemId);
                    charset = await _at.TrySetCharsetAsync("GSM");
                    if (!charset)
                    {
                        _log.LogInformation("Modem {Id}: GSM not supported, trying UCS2...", _modemId);
                        charset = await _at.TrySetCharsetAsync("UCS2");
                    }
                }
                _log.LogInformation("Modem {Id}: CSCS={Result}", _modemId, charset);
            }

            var (existingImsi, existingPhone) = await _db.GetActiveSimInfoAsync(_modemId);
            var phoneNum = existingPhone;

            _log.LogInformation("Modem {Id}: Running *101# to get phone...", _modemId);
            var phone = await _at.GetPhoneNumberViaUssdAsync();
            if (!string.IsNullOrEmpty(phone))
            {
                _log.LogInformation("Modem {Id}: Phone={Phone}", _modemId, phone);
                if (long.TryParse(phone, out var parsed))
                    phoneNum = parsed;
            }
            else
                _ = RetryPhoneAsync(ct);

            try
            {
                _log.LogInformation("Modem {Id}: Running *222# for balance...", _modemId);
                var balance = await _at.GetBalanceAsync();
                if (balance.HasValue)
                {
                    _log.LogInformation("Modem {Id}: Balance={Balance} DZD", _modemId, balance.Value);
                    await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateSimBalance, Data = new { ModemId = _modemId, Balance = balance.Value } });
                }
                else
                {
                    _log.LogInformation("Modem {Id}: *222# returned no balance, will use SMS", _modemId);
                }
                _lastUssdCheck = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Modem {Id}: *222# failed", _modemId);
            }

            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpsertSimCard, Data = new { ModemId = _modemId, IMSI = imsi, PhoneNumber = phoneNum } });

            _simCardId = await ResolveSimCardIdAsync();

            if (!_isHiLink)
            {
                var cpms = await _at.SendCommandAsync("AT+CPMS?");
                _log.LogInformation("Modem {Id}: CPMS? -> {Resp}", _modemId, cpms.ReplaceLineEndings(" "));
                cpms = await _at.SendCommandAsync("AT+CPMS=\"SM\",\"SM\",\"SM\"");
                _log.LogInformation("Modem {Id}: CPMS=SM -> {Resp}", _modemId, cpms.ReplaceLineEndings(" "));
                var cnmi = await _at.SendCommandAsync("AT+CNMI=2,1,0,0,0");
                _log.LogInformation("Modem {Id}: CNMI -> {Resp}", _modemId, cnmi.ReplaceLineEndings(" "));
            }

            var messages = await _at.ReadAllSmsAsync();
            _log.LogInformation("Modem {Id}: {Count} SMS on SIM", _modemId, messages.Count);
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
            _balanceLoop = PeriodicBalanceLoopAsync(
                TimeSpan.FromMinutes(_config.Get<int>("modem.balance.poll.interval", 30)), loopToken);

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
            try
            {
                await _atLock.WaitAsync(ct);
                try { await PollSmsAsync(); }
                finally { _atLock.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Poll loop error", _modemId); }
        }
    }

    private async Task PeriodicBalanceLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }

            if (_disposed) break;
            try
            {
                await _atLock.WaitAsync(ct);
                try { await PeriodicBalanceCheckAsync(); }
                finally { _atLock.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Balance loop error", _modemId); }
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
        if (_balanceLoop != null) try { await _balanceLoop; } catch { }
        try
        {
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = _modemId, Status = ModemStatus.Offline } });
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemComPort, Data = new { ModemId = _modemId, ComPort = (string?)null } });
        }
        catch { }
        _at.Dispose();
    }

    private async Task RetryPhoneAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(15000, ct);
            var phone = await _at.GetPhoneNumberViaUssdAsync();
            if (!string.IsNullOrEmpty(phone) && long.TryParse(phone, out var phoneNum))
            {
                await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateSimCardPhone, Data = new { ModemId = _modemId, PhoneNumber = phoneNum } });
                _log.LogInformation("Modem {Id}: Phone resolved: {Phone}", _modemId, phone);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Phone retry failed", _modemId); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loopCts.Cancel();
        _loopCts.Dispose();
        _at?.Dispose();
    }

    private async Task WatchdogAsync()
    {
        _log.LogInformation("Modem {Id}: Watchdog starting...", _modemId);
        if (_at == null || !_at.IsOpen) { _log.LogInformation("Modem {Id}: Watchdog - port closed", _modemId); return; }

        if (_isHiLink)
        {
            var alive = await _at.IsAliveAsync();
            if (!alive)
            {
                _log.LogWarning("Modem {Id}: HiLink unreachable, disconnecting for re-probe", _modemId);
                await DisconnectAsync();
            }
            return;
        }

        try
        {
            var resp = await _at.SendCommandAsync("AT");
            _log.LogInformation("Modem {Id}: Watchdog - {Resp}", _modemId, resp.ReplaceLineEndings(" "));
            if (!resp.Contains("OK"))
            {
                _log.LogWarning("Modem {Id}: Watchdog AT failed -> disconnecting for re-probe", _modemId);
                await DisconnectAsync();
            }
        }
        catch (IOException) { await DisconnectAsync(); }
        catch (InvalidOperationException) { await DisconnectAsync(); }
        catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Watchdog error -> disconnecting", _modemId); await DisconnectAsync(); }
    }

    private async Task PollSmsAsync()
    {
        if (_at == null || !_at.IsOpen) { _log.LogInformation("Modem {Id}: Poll - port closed", _modemId); return; }
        try
        {
            var messages = await _at.ReadAllSmsAsync();
            var count = messages.Count;
            _log.LogInformation("Modem {Id}: Poll - {Count} SMS found", _modemId, count);

            if (count <= 0) return;

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
                        savedCount++;
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
                _log.LogInformation("Modem {Id}: Poll - {SavedCount} SMS saved to DB, {SkippedCount} skipped (duplicates)", _modemId, savedCount, skippedCount);
                await _at.DeleteAllSmsAsync();

                if (DateTime.UtcNow - _lastUssdCheck > TimeSpan.FromSeconds(60))
                {
                    _lastUssdCheck = DateTime.UtcNow;
                    try
                    {
                        _log.LogInformation("Modem {Id}: SMS received, running *222# for balance check...", _modemId);
                        var balance = await _at.GetBalanceAsync();
                        if (balance.HasValue)
                        {
                            _log.LogInformation("Modem {Id}: Balance: {Balance} DZD", _modemId, balance.Value);
                            await _db.EnqueueAsync(new()
                            {
                                Type = DatabaseWriteChannel.Op.UpdateSimBalance,
                                Data = new { ModemId = _modemId, Balance = balance.Value }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Modem {Id}: *222# balance check failed", _modemId);
                    }
                }
            }
        }
        catch (IOException) { await DisconnectAsync(); }
        catch (InvalidOperationException) { await DisconnectAsync(); }
        catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Poll error", _modemId); }
    }

    private async Task PeriodicBalanceCheckAsync()
    {
        if (_at == null || !_at.IsOpen) return;
        try
        {
            _log.LogInformation("Modem {Id}: Periodic balance check (*222#)...", _modemId);
            var balance = await _at.GetBalanceAsync();
            if (balance.HasValue)
            {
                _log.LogInformation("Modem {Id}: Periodic balance: {Balance} DZD", _modemId, balance.Value);
                await _db.EnqueueAsync(new()
                {
                    Type = DatabaseWriteChannel.Op.UpdateSimBalance,
                    Data = new { ModemId = _modemId, Balance = balance.Value }
                });
            }
            else
            {
                _log.LogWarning("Modem {Id}: Periodic balance check returned no value", _modemId);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modem {Id}: Periodic balance check failed", _modemId);
        }
    }
}
