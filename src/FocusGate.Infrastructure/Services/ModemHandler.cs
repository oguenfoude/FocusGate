using System;
using System.IO;
using FocusGate.Core.DTOs;
using FocusGate.Core.Enums;
using FocusGate.Core.Interfaces;
using FocusGate.Core.Models;
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
    private readonly MeetMobService? _meetMob;
    private long _simCardId;
    private string _imsi = string.Empty;
    private volatile bool _disposed;
    private CancellationTokenSource _loopCts;
    private Task? _watchdogLoop;
    private Task? _pollLoop;
    private Task? _networkRetryLoop;
    private Task? _postStartupBalanceCheckTask;
    private readonly SemaphoreSlim _atLock = new(1, 1);
    private DateTime? _ussdUnavailableSince;
    private int _hiLinkFailureCount;
    private const int HiLinkMaxFailures = 5;
    private ModemStatus _lastWrittenStatus = ModemStatus.Unknown;
    private DateTime _lastHeartbeatWriteUtc = DateTime.MinValue;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DisposeLoopTimeout = TimeSpan.FromSeconds(10);
    private DateTime? _smsCooldownUntil;
    private MeetMobToken? _meetMobToken;
    private DateTime _lastMeetMobRefreshUtc = DateTime.MinValue;
    private DateTime _meetMobNextRetryUtc = DateTime.MinValue;
    private int _meetMobConsecutiveFailures;

    private TimeSpan GetMeetMobBackoffDelay()
    {
        var initialSeconds = _config.Get<int>("meetmob.backoff.initial", 120);
        var maxSeconds = _config.Get<int>("meetmob.backoff.max", 1800);
        if (_meetMobConsecutiveFailures <= 1)
            return TimeSpan.FromSeconds(initialSeconds);
        if (_meetMobConsecutiveFailures == 2)
            return TimeSpan.FromSeconds(300);
        return TimeSpan.FromSeconds(maxSeconds);
    }

    private void RecordMeetMobFailure()
    {
        _meetMobConsecutiveFailures++;
        var delay = GetMeetMobBackoffDelay();
        _meetMobNextRetryUtc = DateTime.UtcNow + delay;
        _log.LogWarning("Modem {Id}: MeetMob failure #{Count} — next retry in {Minutes:F1}min", _modemId, _meetMobConsecutiveFailures, delay.TotalMinutes);
    }

    private void RecordMeetMobSuccess()
    {
        _meetMobConsecutiveFailures = 0;
        _meetMobNextRetryUtc = DateTime.MinValue;
    }

    private readonly IUssdExecutionEngine _ussdEngine;
    private readonly ISmsProcessingEngine _smsEngine;

    public VirtualModemContext Context { get; }
    public bool IsAlive => !_disposed && _at?.IsOpen == true;

    public ModemHandler(
        IAtCommandService at,
        DatabaseWriteChannel db,
        ILogger<ModemHandler> log,
        IConfigProvider config,
        int modemId,
        string comPort,
        bool isHiLink = false,
        MeetMobService? meetMob = null,
        IUssdExecutionEngine? ussdEngine = null,
        ISmsProcessingEngine? smsEngine = null)
    {
        _at = at;
        _db = db;
        _log = log;
        _config = config;
        _modemId = modemId;
        _comPort = comPort;
        _isHiLink = isHiLink;
        _meetMob = meetMob;
        _loopCts = new CancellationTokenSource();
        _ussdEngine = ussdEngine ?? new UssdExecutionEngine(config, db, Microsoft.Extensions.Logging.Abstractions.NullLogger<UssdExecutionEngine>.Instance);
        _smsEngine = smsEngine ?? new SmsProcessingEngine(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<SmsProcessingEngine>.Instance);
        Context = new VirtualModemContext
        {
            ModemId = modemId,
            ComPort = comPort,
            IsHiLink = isHiLink
        };
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
            _imsi = imsi;
            _log.LogInformation("Modem {Id}: IMSI={IMSI}", _modemId, imsi);

            NetworkRegistration netReg = NetworkRegistration.Unknown;
            for (int i = 1; i <= 5; i++)
            {
                netReg = await _at.GetNetworkRegistrationAsync();
                _log.LogInformation("Modem {Id}: Network {Attempt}/5 - {Status}", _modemId, i, netReg);
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

            if (_simCardId <= 0)
            {
                _log.LogWarning("Modem {Id}: Cannot start — SimCardId unresolved after startup", _modemId);
                return false;
            }

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
            var startupRechargeAmounts = new List<decimal>();
            string? startupRechargeContent = null;
            if (messages.Count > 0)
            {
                var smsTypes = new Dictionary<string, int>();
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
                            Content = msg.Content ?? "",
                            ReceivedAt = msg.ReceivedAt
                        },
                        Completed = tcs
                    });
                    var smsType = DatabaseWriteChannel.ClassifySmsType(msg.Sender, msg.Content ?? "");
                    smsTypes[smsType] = smsTypes.GetValueOrDefault(smsType) + 1;
                    if (IsMobilisBalanceTrigger(msg))
                    {
                        startupRechargeContent ??= msg.Content;
                        var amt = DatabaseWriteChannel.ExtractRechargeAmountFromContent(msg.Content ?? "");
                        if (amt.HasValue && amt.Value > 0)
                        {
                            startupRechargeAmounts.Add(amt.Value);
                        }
                    }
                }
                bool[] results;
                try { results = await Task.WhenAll(tcsList); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Modem {Id}: Some startup SMS writes failed — continuing with partial results", _modemId);
                    results = tcsList.Select(t => t.IsCompletedSuccessfully && t.Result).ToArray();
                }
                var savedCount = results.Count(r => r);
                var skippedCount = results.Length - savedCount;
                try { await _at.DeleteAllSmsAsync(); }
                catch (Exception ex) { _log.LogWarning(ex, "Modem {Id}: Startup DeleteAllSms failed", _modemId); }
                var typeBreakdown = string.Join(", ", smsTypes.Select(kvp => $"{kvp.Key}={kvp.Value}"));
                _log.LogInformation("Modem {Id}: Startup SMS processed: {TotalCount} total ({SavedCount} saved, {SkippedCount} skipped) Types: [{Types}]", _modemId, messages.Count, savedCount, skippedCount, typeBreakdown);
            }

            var status = netReg == NetworkRegistration.Registered ? ModemStatus.Online : ModemStatus.PendingNetwork;
            _lastWrittenStatus = status;
            _lastHeartbeatWriteUtc = DateTime.UtcNow;
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = _modemId, Status = status } });

            var loopToken = _loopCts.Token;
            _watchdogLoop = WatchdogLoopAsync(
                TimeSpan.FromSeconds(_config.Get<int>("modem.watchdog.interval", 30)), loopToken);
            _pollLoop = PollSmsLoopAsync(
                TimeSpan.FromSeconds(_config.Get<int>("modem.sms.poll.interval", 30)), loopToken);

            if (status == ModemStatus.Online)
            {
                _postStartupBalanceCheckTask = Task.Run(async () =>
                {
                    try
                    {
                        if (_meetMob != null)
                        {
                            await _meetMob.WarmupAsync(loopToken);
                            await _meetMob.RefreshLock.WaitAsync(loopToken);
                            try
                            {
                                var meetMobOk = await TryMeetMobLoginAndBalanceAsync(loopToken);
                                if (!meetMobOk)
                                {
                                    RecordMeetMobFailure();
                                    _log.LogInformation("Modem {Id}: MeetMob failed at startup — will retry with backoff, using USSD for now", _modemId);
                                    if (startupRechargeContent != null)
                                        await RunBalanceCheckFromSmsAsync(startupRechargeContent, loopToken);
                                    else
                                        await TryGetPhoneAndBalanceAsync(loopToken);
                                }
                            }
                            finally { _meetMob.RefreshLock.Release(); }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _log.LogWarning(ex, "Modem {Id}: Post-startup balance check failed", _modemId); }
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
        _log.LogDebug("Modem {Id}: Watchdog loop started (interval {Interval}s)", _modemId, (int)interval.TotalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }

            if (_disposed) break;
            try
            {
                await _atLock.WaitAsync(ct);
                try { await WatchdogAsync(); }
                finally { SafeReleaseAtLock(); }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Watchdog loop error", _modemId); }

            if (_meetMob != null && !_disposed && _meetMobToken == null)
            {
                if (_meetMobNextRetryUtc != DateTime.MinValue && DateTime.UtcNow >= _meetMobNextRetryUtc)
                {
                    _log.LogInformation("Modem {Id}: MeetMob retry after backoff ({Count} consecutive failures)", _modemId, _meetMobConsecutiveFailures);
                    _meetMobNextRetryUtc = DateTime.MinValue;
                    try
                    {
                        await _meetMob.RefreshLock.WaitAsync(ct);
                        try
                        {
                            var ok = await TryMeetMobLoginAndBalanceAsync(ct);
                            if (!ok) RecordMeetMobFailure();
                        }
                        finally { _meetMob.RefreshLock.Release(); }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _log.LogWarning("Modem {Id}: MeetMob retry failed: {Error}", _modemId, ex.Message); RecordMeetMobFailure(); }
                }
            }
        }
    }

    private async Task PollSmsLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        _log.LogDebug("Modem {Id}: Poll loop started (interval {Interval}s)", _modemId, (int)interval.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }

            if (_disposed) break;

            string? rechargeSmsContent = null;
            try
            {
                await _atLock.WaitAsync(ct);
                try { rechargeSmsContent = await PollSmsAsync(); }
                finally { SafeReleaseAtLock(); }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Poll loop error", _modemId); }

            if (rechargeSmsContent != null && !_disposed)
            {
                try
                {
                    await _atLock.WaitAsync(ct);
                    try { await RunBalanceCheckFromSmsAsync(rechargeSmsContent, ct); }
                    finally { SafeReleaseAtLock(); }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Modem {Id}: SMS-triggered balance check error", _modemId); }
            }
        }
    }

    private async Task RunBalanceCheckFromSmsAsync(string rechargeSmsContent, CancellationToken ct)
    {
        var smsType = DatabaseWriteChannel.ClassifySmsType("Mobilis", rechargeSmsContent);
        var rechargeAmount = DatabaseWriteChannel.ExtractRechargeAmountFromContent(rechargeSmsContent);

        _log.LogInformation("Modem {Id}: Mobilis {SmsType} SMS — recharge amount from SMS: {Amount} — trying MeetMob balance first...",
            _modemId, smsType,
            rechargeAmount.HasValue ? $"{rechargeAmount.Value:F2} DZD" : "not found");

        // --- Step 1: MeetMob balance snapshot (updates SIM balance only) ---
        if (_meetMob != null && _meetMobToken != null)
        {
            var meetMobOk = await TryMeetMobBalanceAsync(ct);
            if (meetMobOk)
            {
                _log.LogInformation("Modem {Id}: MeetMob balance snapshot saved from recharge SMS", _modemId);
                _db.ClearPendingBalanceCheck(_modemId);
                return;
            }
        }

        // --- Step 2: *222# USSD for balance snapshot ---
        _log.LogInformation("Modem {Id}: MeetMob unavailable — falling back to *222# USSD", _modemId);
        var balance = await _at.GetBalanceAsync();
        if (balance.HasValue)
        {
            _log.LogInformation("Modem {Id}: Balance snapshot via *222#: {Balance:F2} DZD", _modemId, balance.Value);
            try
            {
                await _db.EnqueueAsync(new()
                {
                    Type = DatabaseWriteChannel.Op.UpdateSimBalance,
                    Data = new { ModemId = _modemId, Balance = balance.Value }
                });
            }
            catch (Exception ex) { _log.LogDebug(ex, "Modem {Id}: UpdateSimBalance failed", _modemId); }
            _db.ClearPendingBalanceCheck(_modemId);
            return;
        }

        // --- Step 3: MeetMob fresh login + balance ---
        _log.LogInformation("Modem {Id}: *222# returned no balance — trying MeetMob fresh login...", _modemId);
        if (_meetMob != null)
        {
            try
            {
                var simInfo = await _db.GetActiveSimInfoAsync(_modemId);
                var freshPhone = MeetMobService.FormatPhone(simInfo.PhoneNumber);
                if (!string.IsNullOrEmpty(freshPhone))
                {
                    var freshOk = await TryMeetMobLoginAndBalanceInnerAsync(freshPhone, acquireAtLock: false, ct);
                    if (freshOk)
                    {
                        _log.LogInformation("Modem {Id}: MeetMob fresh login balance snapshot saved", _modemId);
                        _db.ClearPendingBalanceCheck(_modemId);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogWarning("Modem {Id}: MeetMob fresh login failed: {Error}", _modemId, ex.Message); }
        }

        _db.ClearPendingBalanceCheck(_modemId);
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
        _lastWrittenStatus = ModemStatus.Offline;
        _loopCts.Cancel();
        try
        {
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = _modemId, Status = ModemStatus.Offline } });
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemComPort, Data = new { ModemId = _modemId, ComPort = (string?)null } });
        }
        catch (Exception ex) { _log.LogDebug(ex, "Modem {Id}: Failed to enqueue offline status during disconnect", _modemId); }
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) { _log.LogDebug(ex, "Modem {Id}: DisconnectAsync failed during dispose", _modemId); }

        if (!_disposed) _disposed = true;

        try { _loopCts.Cancel(); } catch { }
        try
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var tasks = new List<Task>();
                    if (_watchdogLoop != null) tasks.Add(_watchdogLoop);
                    if (_pollLoop != null) tasks.Add(_pollLoop);
                    if (_networkRetryLoop != null) tasks.Add(_networkRetryLoop);
                    if (_postStartupBalanceCheckTask != null) tasks.Add(_postStartupBalanceCheckTask);
                    if (tasks.Count > 0)
                    {
                        try { await Task.WhenAll(tasks); }
                        catch (OperationCanceledException) { }
                    }
                }
                catch (Exception ex) { _log.LogDebug(ex, "Modem {Id}: Loop shutdown error", _modemId); }
            });
            task.Wait(DisposeLoopTimeout);
        }
        catch { }
        try { _loopCts.Dispose(); } catch { }
        try { _atLock.Dispose(); } catch { }
        try { _at?.Dispose(); } catch { }
    }

    private void SafeReleaseAtLock()
    {
        try { _atLock.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private async Task WatchdogAsync()
    {
        if (_at == null || !_at.IsOpen) {
            _log.LogDebug("Modem {Id}: Watchdog skipped (port not open)", _modemId);
            return;
        }

            if (_isHiLink)
            {
                var refreshed = await _at.TryRefreshSessionAsync();
                if (refreshed && !_at.LastRequestFailed)
                {
                    _hiLinkFailureCount = 0;
                    await WriteStatusIfChangedAsync(ModemStatus.Online);
                    return;
                }

                _log.LogWarning("Modem {Id}: Session refresh failed, trying alive check fallback", _modemId);
                var alive = await _at.IsAliveAsync();
                if (alive && !_at.LastRequestFailed)
                {
                    _hiLinkFailureCount = 0;
                    await WriteStatusIfChangedAsync(ModemStatus.Online);
                }
                else
                {
                    _hiLinkFailureCount++;
                    _log.LogWarning("Modem {Id}: Alive check also failed ({Count}/{Max})",
                        _modemId, _hiLinkFailureCount, HiLinkMaxFailures);

                    if (_hiLinkFailureCount >= HiLinkMaxFailures)
                    {
                        _log.LogWarning("Modem {Id}: HiLink unreachable after {Max} consecutive failures — disconnecting for re-probe", _modemId, HiLinkMaxFailures);
                        await DisconnectAsync();
                    }
                    else
                    {
                        await WriteStatusIfChangedAsync(ModemStatus.PendingNetwork);
                    }
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
                await WriteStatusIfChangedAsync(ModemStatus.Online);
            }
        }
        catch (IOException) { await DisconnectAsync(); }
        catch (InvalidOperationException) { await DisconnectAsync(); }
        catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Watchdog error -> disconnecting", _modemId); await DisconnectAsync(); }
    }

    private async Task WriteStatusIfChangedAsync(ModemStatus status)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var statusChanged = status != _lastWrittenStatus;
        var heartbeatDue = (now - _lastHeartbeatWriteUtc) >= HeartbeatInterval;

        if (!statusChanged && !heartbeatDue)
            return;

        _lastWrittenStatus = status;
        _lastHeartbeatWriteUtc = now;

        if (!statusChanged)
        {
            _log.LogDebug("Modem {Id}: Heartbeat due, touching UpdatedAt", _modemId);
            try
            {
                await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.TouchModemUpdatedAt, Data = new { ModemId = _modemId } });
            }
            catch (Exception ex) { _log.LogDebug(ex, "Modem {Id}: TouchModemUpdatedAt failed", _modemId); }
            return;
        }

        _log.LogInformation("Modem {Id}: Status={Status} (changed={Changed}), writing to DB", _modemId, status, statusChanged);

        try
        {
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateModemStatus, Data = new { ModemId = _modemId, Status = status } });
        }
        catch (Exception ex) { _log.LogDebug(ex, "Modem {Id}: UpdateModemStatus failed", _modemId); }
    }

    private async Task<string?> PollSmsAsync()
    {
        if (_at == null || !_at.IsOpen) return null;

        if (_smsCooldownUntil.HasValue && DateTime.UtcNow < _smsCooldownUntil.Value)
            return null;

        string? rechargeSmsContent = null;
        try
        {
            List<RawSmsMessage> messages;
            try
            {
                messages = await _at.ReadAllSmsAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Modem {Id}: Poll - ReadAllSms HTTP failed, disconnecting for re-handshake", _modemId);
                await DisconnectAsync();
                return null;
            }

            if (_at.IsSmsInboxFull)
            {
        _log.LogWarning("Modem {Id}: SMS inbox full (125002) — clearing inbox, backing off 30s", _modemId);
        try { await _at.DeleteAllSmsAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Modem {Id}: DeleteAllSms after 125002 failed", _modemId); }
        _smsCooldownUntil = DateTime.UtcNow.AddSeconds(30);
                return null;
            }

            if (messages.Count <= 0)
            {
                _log.LogDebug("Modem {Id}: Poll - 0 SMS on SIM", _modemId);
                return null;
            }

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
                            Content = msg.Content ?? "",
                            ReceivedAt = msg.ReceivedAt
                        },
                        Completed = tcs
                    });
                    var wasSaved = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (wasSaved)
                        savedCount++;
                    else
                        skippedCount++;

                    var smsType = DatabaseWriteChannel.ClassifySmsType(msg.Sender, msg.Content ?? "");
                    if (rechargeSmsContent == null && IsMobilisBalanceTrigger(msg))
                    {
                        rechargeSmsContent = msg.Content;
                        _log.LogInformation("Modem {Id}: Mobilis {SmsType} trigger detected — will run *222# after poll", _modemId, smsType);
                    }
                }
                catch (TimeoutException) when (_disposed) { }
                catch (TimeoutException)
                {
                    _log.LogDebug("Modem {Id}: SMS DB write timed out (5s) — treating as skipped for {Sender}", _modemId, msg.Sender);
                    skippedCount++;
                }
                catch (OperationCanceledException) when (_disposed) { }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Modem {Id}: Failed to process SMS from {Sender}", _modemId, msg.Sender);
                    skippedCount++;
                }
            }

            if (savedCount > 0 || skippedCount > 0)
            {
                _log.LogInformation("Modem {Id}: Poll - {SavedCount} SMS saved, {SkippedCount} skipped", _modemId, savedCount, skippedCount);
                try { await _at.DeleteAllSmsAsync(); }
                catch (Exception ex) { _log.LogWarning(ex, "Modem {Id}: DeleteAllSms after poll failed", _modemId); }
            }
        }
        catch (IOException) { await DisconnectAsync(); }
        catch (InvalidOperationException) { await DisconnectAsync(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Poll error", _modemId); }
        return rechargeSmsContent;
    }

    internal static bool IsMobilisBalanceTrigger(RawSmsMessage msg)
    {
        var sender = msg.Sender.Trim();
        if (!DatabaseWriteChannel.IsMobilisSender(sender)) return false;
        if (msg.Content.Contains("montant de", StringComparison.OrdinalIgnoreCase)
            && msg.Content.Contains("reçu", StringComparison.OrdinalIgnoreCase))
            return true;
        if (msg.Content.Contains("rechargé", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private async Task<bool> TryMeetMobLoginAndBalanceAsync(CancellationToken ct)
    {
        if (_meetMob == null) return false;
        if (string.IsNullOrEmpty(_imsi) || _imsi.Length < 5) return false;

        var existingPhone = (await _db.GetActiveSimInfoAsync(_modemId)).PhoneNumber;
        if (existingPhone == 0)
        {
            _log.LogInformation("Modem {Id}: No phone number yet — fetching via *101# for MeetMob login", _modemId);
            try
            {
                await _atLock.WaitAsync(ct);
                try
                {
                    var phoneStr = await _at.GetPhoneNumberViaUssdAsync();
                    if (!string.IsNullOrEmpty(phoneStr) && long.TryParse(phoneStr, out var phoneNum) && phoneNum > 0)
                    {
                        existingPhone = phoneNum;
                        await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateSimCardPhone, Data = new { ModemId = _modemId, PhoneNumber = phoneNum } });
                        _log.LogInformation("Modem {Id}: Got phone number {Phone} via *101#", _modemId, phoneNum);
                    }
                }
                finally { SafeReleaseAtLock(); }
            }
            catch (OperationCanceledException) { return false; }
            catch (ObjectDisposedException) { return false; }
            catch (Exception ex) { _log.LogWarning(ex, "Modem {Id}: Failed to fetch phone via *101#", _modemId); }

            if (existingPhone == 0)
            {
                _log.LogDebug("Modem {Id}: MeetMob skipped — could not fetch phone number", _modemId);
                return false;
            }
        }

        var phone = MeetMobService.FormatPhone(existingPhone);
        if (string.IsNullOrEmpty(phone))
        {
            _log.LogDebug("Modem {Id}: MeetMob skipped — invalid phone format", _modemId);
            return false;
        }

        if (!_meetMob.CanRetry(phone))
        {
            _log.LogDebug("Modem {Id}: MeetMob in cooldown, skipping", _modemId);
            return false;
        }

        return await TryMeetMobLoginAndBalanceInnerAsync(phone, acquireAtLock: true, ct);
    }

    private async Task<bool> TryMeetMobLoginAndBalanceInnerAsync(string phone, bool acquireAtLock, CancellationToken ct)
    {
        if (_meetMob == null) return false;
        var cachedToken = await _meetMob.GetValidTokenAsync(phone);
        if (cachedToken != null)
        {
            _log.LogInformation("Modem {Id}: MeetMob using cached token", _modemId);
            _meetMobToken = cachedToken;
            _lastMeetMobRefreshUtc = DateTime.UtcNow;
            var cachedBalanceOk = await TryMeetMobBalanceAsync(ct);
            if (cachedBalanceOk) return true;

            if (_meetMob.WasLastRequestNetworkError())
            {
                _log.LogWarning("Modem {Id}: MeetMob cached token balance failed (network error) — token preserved, falling back to USSD", _modemId);
                return false;
            }

            _log.LogWarning("Modem {Id}: MeetMob cached token balance failed (session expired: {Error}) — invalidating and retrying fresh login", _modemId, _meetMob.GetLastErrorCode() ?? "auth error");
            await _meetMob.InvalidateTokenAsync(phone);
            _meetMobToken = null;
        }

        if (_meetMob.IsWafBlocked())
        {
            _log.LogWarning("Modem {Id}: MeetMob WAF is blocking — skipping fresh login", _modemId);
            return false;
        }

        _log.LogInformation("Modem {Id}: MeetMob logging in via OTP...", _modemId);
        MeetMobLoginResult result;
        try
        {
            if (acquireAtLock)
            {
                await _atLock.WaitAsync(ct);
                try { result = await _meetMob.LoginAsync(_imsi, phone, _at, ct); }
                finally { SafeReleaseAtLock(); }
            }
            else
            {
                result = await _meetMob.LoginAsync(_imsi, phone, _at, ct);
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException) { return false; }

        if (!result.Success)
        {
            _log.LogWarning("Modem {Id}: MeetMob login failed — {Error}, falling back to USSD", _modemId, result.Error);
            _meetMob.SetCooldown(phone, _config.Get<int>("meetmob.fallback_cooldown", 150));
            return false;
        }

        _meetMobToken = result.Token;
        _lastMeetMobRefreshUtc = DateTime.UtcNow;
        _log.LogInformation("Modem {Id}: MeetMob login success", _modemId);
        await Task.Delay(2000, ct);
        return await TryMeetMobBalanceAsync(ct);
    }

    private async Task<bool> TryMeetMobBalanceAsync(CancellationToken ct)
    {
        if (_meetMob == null || _meetMobToken == null) return false;
        try
        {
            var balance = await _meetMob.GetBalanceAsync(_imsi, _meetMobToken, ct);
            if (balance.HasValue)
            {
                _log.LogInformation("Modem {Id}: MeetMob balance: {Balance:F2} DZD", _modemId, balance.Value);
                RecordMeetMobSuccess();
                await _db.EnqueueAsync(new()
                {
                    Type = DatabaseWriteChannel.Op.UpdateSimBalance,
                    Data = new { ModemId = _modemId, Balance = balance.Value, Source = BalanceSource.MeetMob }
                });
                return true;
            }

            if (_meetMob.IsWafBlocked())
            {
                _log.LogWarning("Modem {Id}: MeetMob balance null but WAF is blocking — skipping re-login (token preserved)", _modemId);
                return false;
            }
            if (_meetMob.WasLastRequestNetworkError())
            {
                _log.LogWarning("Modem {Id}: MeetMob balance null due to network error (server down/timeout) — skipping re-login (token preserved)", _modemId);
                return false;
            }

            var errorCode = _meetMob.GetLastErrorCode();
            if (string.IsNullOrEmpty(_meetMobToken.AccountId) && errorCode != "MSF.100010" && errorCode != "120")
            {
                _log.LogWarning("Modem {Id}: MeetMob balance null (accountId empty) — skipping re-login (token preserved)", _modemId);
                return false;
            }

            _log.LogWarning("Modem {Id}: MeetMob balance returned null (error={Error}) — session may be expired, attempting re-login", _modemId, errorCode ?? "unknown");
            await _meetMob.InvalidateTokenAsync(_meetMobToken.Phone);
            _meetMobToken = null;

            var phoneRaw = await _db.GetPhoneNumberAsync(_imsi);
            if (string.IsNullOrEmpty(phoneRaw))
            {
                _log.LogWarning("Modem {Id}: No phone number for re-login", _modemId);
                return false;
            }

            var phone = long.TryParse(phoneRaw, out var phoneLong)
                ? MeetMobService.FormatPhone(phoneLong) ?? phoneRaw
                : phoneRaw;

            MeetMobLoginResult loginResult;
            try
            {
                await _atLock.WaitAsync(ct);
                try { loginResult = await _meetMob.LoginAsync(_imsi, phone, _at, ct); }
                finally { SafeReleaseAtLock(); }
            }
            catch (OperationCanceledException) { return false; }
            catch (ObjectDisposedException) { return false; }

            if (!loginResult.Success)
            {
                _log.LogWarning("Modem {Id}: MeetMob re-login failed — {Error}", _modemId, loginResult.Error);
                _meetMob.SetCooldown(phone, _config.Get<int>("meetmob.fallback_cooldown", 150));
                return false;
            }

            _meetMobToken = loginResult.Token;
            _lastMeetMobRefreshUtc = DateTime.UtcNow;
            _log.LogInformation("Modem {Id}: MeetMob re-login success — retrying balance", _modemId);
            await Task.Delay(2000, ct);

            if (_meetMobToken == null)
            {
                _log.LogWarning("Modem {Id}: MeetMob re-login returned null token", _modemId);
                return false;
            }

            if (_meetMob.IsWafBlocked())
            {
                _log.LogWarning("Modem {Id}: MeetMob WAF blocked after re-login — giving up this cycle", _modemId);
                return false;
            }

            var retryBalance = await _meetMob.GetBalanceAsync(_imsi, _meetMobToken, ct);
            if (retryBalance.HasValue)
            {
                _log.LogInformation("Modem {Id}: MeetMob balance after re-login: {Balance:F2} DZD", _modemId, retryBalance.Value);
                RecordMeetMobSuccess();
                await _db.EnqueueAsync(new()
                {
                    Type = DatabaseWriteChannel.Op.UpdateSimBalance,
                    Data = new { ModemId = _modemId, Balance = retryBalance.Value, Source = BalanceSource.MeetMob }
                });
                return true;
            }

            _log.LogWarning("Modem {Id}: MeetMob balance still null after re-login", _modemId);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Modem {Id}: MeetMob balance fetch failed: {Error}", _modemId, ex.Message);
        }
        return false;
    }

    private async Task TryMeetMobHistoryAsync(CancellationToken ct)
    {
        if (_meetMob == null || _meetMobToken == null) return;
        try
        {
            var records = await _meetMob.GetRechargeHistoryAsync(_meetMobToken, ct);
            if (records.Count == 0)
            {
                _log.LogDebug("Modem {Id}: MeetMob history returned 0 records", _modemId);
                return;
            }

            _log.LogInformation("Modem {Id}: MeetMob history has {Count} records — saving to DB", _modemId, records.Count);
            await _db.EnqueueAsync(new()
            {
                Type = DatabaseWriteChannel.Op.InsertMeetMobHistory,
                Data = new
                {
                    ModemId = _modemId,
                    Imsi = _imsi,
                    Records = records.Select(r => new { TradeTime = r.TradeTime, Amount = r.Amount }).ToList()
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning("Modem {Id}: MeetMob history fetch failed: {Error}", _modemId, ex.Message);
        }
    }

    private async Task TryGetPhoneAndBalanceAsync(CancellationToken ct)
    {
        if (_ussdUnavailableSince.HasValue && DateTime.UtcNow - _ussdUnavailableSince.Value < TimeSpan.FromMinutes(10))
            return;
        _ussdUnavailableSince = null;
        try
        {
            await _atLock.WaitAsync(ct);
            try { await TryGetPhoneAndBalanceInnerAsync(ct); }
            finally { SafeReleaseAtLock(); }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _log.LogError(ex, "Modem {Id}: USSD phone/balance error", _modemId); _ussdUnavailableSince = DateTime.UtcNow; }
    }

    private async Task TryGetPhoneAndBalanceInnerAsync(CancellationToken ct)
    {
        var existingPhone = (await _db.GetActiveSimInfoAsync(_modemId)).PhoneNumber;
        if (existingPhone == 0)
        {
            _log.LogDebug("Modem {Id}: Running USSD *101# for phone number...", _modemId);
            var phone = await _at.GetPhoneNumberViaUssdAsync();
            if (!string.IsNullOrEmpty(phone))
            {
                _log.LogInformation("Modem {Id}: Phone number: {Phone}", _modemId, phone);
                if (long.TryParse(phone, out var phoneNum))
                    await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateSimCardPhone, Data = new { ModemId = _modemId, PhoneNumber = phoneNum } });
                else
                    _log.LogWarning("Modem {Id}: Phone number not numeric: {Phone}", _modemId, phone);
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
            _db.MarkPendingBalanceCheck(_modemId);
            _log.LogInformation("Modem {Id}: *222# returned no balance — pending balance check set", _modemId);
        }
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

                    await WriteStatusIfChangedAsync(ModemStatus.Online);
                }
                finally { SafeReleaseAtLock(); }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Modem {Id}: Network retry loop error", _modemId); }
        }
    }
}
