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
    private Task? _meetMobCheckLoop;
    private Task? _postStartupBalanceCheckTask;
    private Task? _cleanupLoop;
    private readonly SemaphoreSlim _atLock = new(1, 1);
    private DateTime? _ussdUnavailableSince;
    private int _hiLinkFailureCount;
    private const int HiLinkMaxFailures = 5;
    private ModemStatus _lastWrittenStatus = ModemStatus.Unknown;
    private DateTime _lastHeartbeatWriteUtc = DateTime.MinValue;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DisposeLoopTimeout = TimeSpan.FromSeconds(10);
    private DateTime? _smsCooldownUntil;
    private MeetMobToken? _meetMobToken;
    private DateTime _lastMeetMobRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan MeetMobTokenProactiveRefreshWindow = TimeSpan.FromMinutes(5);
    private string _meetMobPhone = string.Empty;
    private decimal? _lastBalance;
    private volatile bool _postStartupDone;

    private bool IsMeetMobTokenExpiringSoon()
    {
        if (_meetMobToken == null) return false;
        var timeUntilExpiry = _meetMobToken.ExpiresAt - DateTime.UtcNow;
        return timeUntilExpiry <= MeetMobTokenProactiveRefreshWindow;
    }

    private bool IsMeetMobTokenExpired()
    {
        if (_meetMobToken == null) return false;
        return _meetMobToken.ExpiresAt <= DateTime.UtcNow;
    }

    private async Task<bool> TryProactiveMeetMobRefreshAsync(CancellationToken ct)
    {
        if (_meetMob == null) return false;
        if (_meetMobToken == null) return false;

        var phoneRaw = await _db.GetPhoneNumberAsync(_imsi);
        if (string.IsNullOrEmpty(phoneRaw))
        {
            _log.LogDebug("Modem {Id}: Proactive MeetMob refresh skipped — no phone number", _modemId);
            return false;
        }

        var phone = long.TryParse(phoneRaw, out var phoneLong)
            ? MeetMobService.FormatPhone(phoneLong) ?? phoneRaw
            : phoneRaw;

        if (!_meetMob.CanRetry(phone))
        {
            _log.LogDebug("Modem {Id}: Proactive MeetMob refresh skipped — in cooldown", _modemId);
            return false;
        }

        _log.LogInformation("Modem {Id}: Proactively refreshing MeetMob token (expires in {Minutes:F1}min)",
            _modemId, (_meetMobToken.ExpiresAt - DateTime.UtcNow).TotalMinutes);

        try
        {
            await _atLock.WaitAsync(ct);
            try
            {
                var result = await _meetMob.LoginAsync(_imsi, phone, _at, ct);
                if (result.Success && result.Token != null)
                {
                    _meetMobToken = result.Token;
                    _lastMeetMobRefreshUtc = DateTime.UtcNow;
                    _log.LogInformation("Modem {Id}: MeetMob proactive refresh success — new token expires at {Expiry}",
                        _modemId, result.Token.ExpiresAt);
                    return true;
                }
                _log.LogWarning("Modem {Id}: MeetMob proactive refresh failed — {Error}", _modemId, result.Error);
                return false;
            }
            finally { SafeReleaseAtLock(); }
        }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException) { return false; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modem {Id}: MeetMob proactive refresh error", _modemId);
            return false;
        }
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

            _log.LogDebug("Modem {Id}: Waiting 2s for network...", _modemId);
            await Task.Delay(2000, ct);

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
                TimeSpan.FromSeconds(_config.Get<int>("modem.sms.poll.interval", 5)), loopToken);

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
                                // Login only — balance check is handled by the check loop
                                var loginOk = await TryMeetMobLoginAsync(loopToken);
                                if (!loginOk)
                                {
                                    _log.LogInformation("Modem {Id}: MeetMob login failed at startup — check loop will retry, using USSD for now", _modemId);
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
                    catch (Exception ex) { _log.LogWarning(ex, "Modem {Id}: Post-startup check failed", _modemId); }
                    finally { _postStartupDone = true; }
                }, loopToken);
            }

            _networkRetryLoop = NetworkRetryLoopAsync(loopToken);
            _meetMobCheckLoop = MeetMobCheckLoopAsync(loopToken);
            _cleanupLoop = CleanupLoopAsync(loopToken);

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

            // Recharge SMS detected — trigger immediate MeetMob balance + history check
            if (rechargeSmsContent != null && !_disposed && _meetMob != null && _postStartupDone)
            {
                _log.LogInformation("Modem {Id}: Recharge SMS detected — triggering immediate MeetMob check", _modemId);
                try
                {
                    if (_meetMobToken == null || IsMeetMobTokenExpired())
                    {
                        var loginOk = await TryMeetMobLoginAsync(ct);
                        if (!loginOk)
                            _log.LogWarning("Modem {Id}: MeetMob login failed for SMS-triggered check — regular cycle will retry", _modemId);
                    }
                    await CheckBalanceAndHistoryAsync(ct, includeHistory: true);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Modem {Id}: SMS-triggered MeetMob check failed", _modemId);
                }
            }
        }
    }

    private async Task MeetMobCheckLoopAsync(CancellationToken ct)
    {
        var checkInterval = _config.Get<int>("meetmob.check.interval", 60);
        _log.LogDebug("Modem {Id}: MeetMob check loop started (interval {Interval}s)", _modemId, checkInterval);

        // Per-modem stagger: add random jitter (0-10s) to avoid all modems hitting server simultaneously
        var jitter = Random.Shared.Next(0, 10);
        _log.LogDebug("Modem {Id}: Staggering MeetMob check loop by {Jitter}s", _modemId, jitter);
        try { await Task.Delay(TimeSpan.FromSeconds(jitter), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(checkInterval), ct); }
            catch (OperationCanceledException) { break; }

            if (_disposed || _meetMob == null) break;

            // Skip during post-startup phase to avoid duplicate balance calls
            if (!_postStartupDone) continue;

            try
            {
                if (!string.IsNullOrEmpty(_meetMobPhone) && !_meetMob.CanRetry(_meetMobPhone))
                {
                    _log.LogDebug("Modem {Id} [{Phone}]: MeetMob in cooldown, skipping", _modemId, _meetMobPhone);
                    continue;
                }

                if (_meetMob.IsWafBlocked(_meetMobPhone))
                {
                    var remaining = _meetMob.GetWafCooldownRemaining(_meetMobPhone);
                    _log.LogWarning("Modem {Id} [{Phone}]: MeetMob WAF blocking — waiting {Remaining}s for cooldown",
                        _modemId, _meetMobPhone, (int)remaining.TotalSeconds);
                    try { await Task.Delay(remaining, ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                if (_meetMobToken == null || IsMeetMobTokenExpired())
                {
                    _log.LogInformation("Modem {Id} [{Phone}]: MeetMob token expired — logging in", _modemId, _meetMobPhone);
                    var loginOk = await TryMeetMobLoginAsync(ct);
                    if (!loginOk)
                    {
                        _meetMob.SetCooldown(_meetMobPhone, 5);
                        continue;
                    }
                }
                else if (IsMeetMobTokenExpiringSoon())
                {
                    _log.LogDebug("Modem {Id} [{Phone}]: MeetMob token expiring soon — proactive refresh", _modemId, _meetMobPhone);
                    await TryProactiveMeetMobRefreshAsync(ct);
                }

                // Balance + history every cycle for faster recharge detection
                await CheckBalanceAndHistoryAsync(ct, includeHistory: true);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Modem {Id}: MeetMob check failed", _modemId); }
        }
    }

    private async Task<bool> TryMeetMobLoginAsync(CancellationToken ct)
    {
        if (_meetMob == null) return false;

        // Try cached token first
        var phoneRaw = await _db.GetPhoneNumberAsync(_imsi);
        if (string.IsNullOrEmpty(phoneRaw))
        {
            // Need to get phone via USSD first
            try
            {
                await _atLock.WaitAsync(ct);
                try
                {
                    var phoneViaUssd = await _at.GetPhoneNumberViaUssdAsync();
                    if (!string.IsNullOrEmpty(phoneViaUssd))
                    {
                        await _db.EnqueueAsync(new()
                        {
                            Type = DatabaseWriteChannel.Op.UpsertSimCard,
                            Data = new { ModemId = _modemId, PhoneNumber = phoneViaUssd }
                        });
                        phoneRaw = phoneViaUssd;
                    }
                }
                finally { SafeReleaseAtLock(); }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Modem {Id}: Failed to fetch phone via USSD", _modemId); }
        }

        if (string.IsNullOrEmpty(phoneRaw))
        {
            _log.LogDebug("Modem {Id}: MeetMob login skipped — no phone number", _modemId);
            return false;
        }

        var phone = long.TryParse(phoneRaw, out var phoneLong)
            ? MeetMobService.FormatPhone(phoneLong) ?? phoneRaw
            : phoneRaw;

        _meetMobPhone = phone;

        // Try cached token
        var cachedToken = await _meetMob.GetValidTokenAsync(phone);
        if (cachedToken != null)
        {
            _meetMobToken = cachedToken;
            _lastMeetMobRefreshUtc = DateTime.UtcNow;
            _log.LogDebug("Modem {Id} [{Phone}]: MeetMob using cached token", _modemId, phone);
            return true;
        }

        // Need fresh login
        if (_meetMob.IsWafBlocked(phone))
        {
            _log.LogWarning("Modem {Id} [{Phone}]: MeetMob WAF is blocking — skipping login", _modemId, phone);
            return false;
        }

        _log.LogInformation("Modem {Id} [{Phone}]: MeetMob logging in via OTP...", _modemId, phone);
        MeetMobLoginResult result;
        try
        {
            await _atLock.WaitAsync(ct);
            try { result = await _meetMob.LoginAsync(_imsi, phone, _at, ct); }
            finally { SafeReleaseAtLock(); }
        }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException) { return false; }

        if (!result.Success)
        {
            _log.LogWarning("Modem {Id} [{Phone}]: MeetMob login failed — {Error}", _modemId, phone, result.Error);
            return false;
        }

        _meetMobToken = result.Token;
        _lastMeetMobRefreshUtc = DateTime.UtcNow;
        _log.LogInformation("Modem {Id} [{Phone}]: MeetMob login success — accountId={AccountId}", _modemId, phone, result.Token?.AccountId ?? "?");
        return true;
    }

    private async Task CheckBalanceAndHistoryAsync(CancellationToken ct, bool includeHistory = true)
    {
        if (_meetMob == null || _meetMobToken == null) return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var balance = await _meetMob.GetBalanceAsync(_meetMobToken, timeout.Token);
            if (balance.HasValue)
            {
                _lastBalance = balance.Value;
                _log.LogInformation("Modem {Id} [{Phone}]: Balance = {Balance} DZD (MeetMob)", _modemId, _meetMobPhone, balance.Value);
                await _db.EnqueueAsync(new()
                {
                    Type = DatabaseWriteChannel.Op.UpdateSimBalance,
                    Data = new { ModemId = _modemId, Balance = balance.Value, Source = BalanceSource.MeetMob }
                });
            }
            else if (_meetMob.WasLastRequestNetworkError(_meetMobPhone))
            {
                _log.LogWarning("Modem {Id} [{Phone}]: MeetMob balance network error — short cooldown", _modemId, _meetMobPhone);
                _meetMob.SetCooldown(_meetMobPhone, 30);
            }
            else if (_meetMob.IsWafBlocked(_meetMobPhone))
            {
                _log.LogWarning("Modem {Id} [{Phone}]: MeetMob WAF blocking balance check", _modemId, _meetMobPhone);
            }
            else
            {
                _log.LogWarning("Modem {Id} [{Phone}]: MeetMob balance null — session expired, will re-login", _modemId, _meetMobPhone);
                await _meetMob.InvalidateTokenAsync(_meetMobToken.Phone);
                _meetMobToken = null;
            }

            if (includeHistory)
                await TryMeetMobHistoryAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Modem {Id}: MeetMob balance check timed out (15s) — short cooldown", _modemId);
            _meetMob?.SetCooldown(_meetMobPhone, 30);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modem {Id}: MeetMob balance/history check failed", _modemId);
        }
    }

    private async Task RunBalanceCheckFromSmsAsync(string rechargeSmsContent, CancellationToken ct)
    {
        var rechargeAmount = DatabaseWriteChannel.ExtractRechargeAmountFromContent(rechargeSmsContent);

        _log.LogInformation("Modem {Id}: Recharge SMS — amount {Amount} — checking MeetMob balance...",
            _modemId, rechargeAmount.HasValue ? $"{rechargeAmount.Value:F2} DZD" : "not found");

        // --- Step 1: MeetMob balance with existing token ---
        if (_meetMob != null && _meetMobToken != null)
        {
            var meetMobOk = await TryMeetMobBalanceAsync(ct);
            if (meetMobOk)
            {
                _log.LogDebug("Modem {Id}: MeetMob balance snapshot saved", _modemId);
                return;
            }
        }

        // --- Step 2: MeetMob fresh login + balance ---
        _log.LogDebug("Modem {Id}: MeetMob token invalid — trying fresh login...", _modemId);
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
                        _log.LogDebug("Modem {Id}: MeetMob fresh login balance snapshot saved", _modemId);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogWarning("Modem {Id}: MeetMob fresh login failed: {Error}", _modemId, ex.Message); }
        }

        // --- Step 3: USSD *222# fallback (MeetMob unavailable) ---
        _log.LogDebug("Modem {Id}: MeetMob unavailable — falling back to *222# USSD", _modemId);
        var balance = await _at.GetBalanceAsync();
        if (balance.HasValue)
        {
            _lastBalance = balance.Value;
            try
            {
                await _db.EnqueueAsync(new()
                {
                    Type = DatabaseWriteChannel.Op.UpdateSimBalance,
                    Data = new { ModemId = _modemId, Balance = balance.Value }
                });
            }
            catch (Exception ex) { _log.LogDebug(ex, "Modem {Id}: UpdateSimBalance failed", _modemId); }
            return;
        }

        _log.LogWarning("Modem {Id}: All balance check methods failed — SIM balance not updated", _modemId);
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
                    if (_meetMobCheckLoop != null) tasks.Add(_meetMobCheckLoop);
                    if (_networkRetryLoop != null) tasks.Add(_networkRetryLoop);
                    if (_postStartupBalanceCheckTask != null) tasks.Add(_postStartupBalanceCheckTask);
                    if (_cleanupLoop != null) tasks.Add(_cleanupLoop);
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
                        var amt = DatabaseWriteChannel.ExtractRechargeAmountFromContent(msg.Content ?? "");
                        _log.LogInformation("Modem {Id}: RECHARGE SMS detected [{SmsType}] amount={Amount} — will run MeetMob check after poll",
                            _modemId, smsType, amt.HasValue ? $"{amt.Value:F2} DZD" : "unknown");
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
            _log.LogDebug("Modem {Id} [{Phone}]: MeetMob using cached token", _modemId, phone);
            _meetMobToken = cachedToken;
            _lastMeetMobRefreshUtc = DateTime.UtcNow;
            var cachedBalanceOk = await TryMeetMobBalanceAsync(ct);
            if (cachedBalanceOk) return true;

            if (_meetMob.WasLastRequestNetworkError(phone))
            {
                _log.LogWarning("Modem {Id} [{Phone}]: MeetMob cached token balance failed (network error) — token preserved, falling back to USSD", _modemId, phone);
                return false;
            }

            _log.LogWarning("Modem {Id} [{Phone}]: MeetMob cached token balance failed (session expired: {Error}) — invalidating and retrying fresh login", _modemId, phone, _meetMob.GetLastErrorCode(phone) ?? "auth error");
            await _meetMob.InvalidateTokenAsync(phone);
            _meetMobToken = null;
        }

        if (_meetMob.IsWafBlocked(phone))
        {
            _log.LogWarning("Modem {Id} [{Phone}]: MeetMob WAF is blocking — skipping fresh login", _modemId, phone);
            return false;
        }

        _log.LogInformation("Modem {Id} [{Phone}]: MeetMob logging in via OTP...", _modemId, phone);
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
            _log.LogWarning("Modem {Id} [{Phone}]: MeetMob login failed — {Error}, falling back to USSD", _modemId, phone, result.Error);
            _meetMob.SetCooldown(phone, _config.Get<int>("meetmob.fallback_cooldown", 5));
            return false;
        }

        _meetMobToken = result.Token;
        _lastMeetMobRefreshUtc = DateTime.UtcNow;
        _log.LogInformation("Modem {Id} [{Phone}]: MeetMob login success", _modemId, phone);
        return await TryMeetMobBalanceAsync(ct);
    }

    private async Task<bool> TryMeetMobBalanceAsync(CancellationToken ct)
    {
        if (_meetMob == null || _meetMobToken == null) return false;
        var phone = _meetMobToken.Phone ?? _meetMobPhone;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var balance = await _meetMob.GetBalanceAsync(_meetMobToken, timeout.Token);
            if (balance.HasValue)
            {
                _lastBalance = balance.Value;
                _log.LogInformation("Modem {Id} [{Phone}]: Balance = {Balance} DZD (MeetMob)", _modemId, phone, balance.Value);
                await _db.EnqueueAsync(new()
                {
                    Type = DatabaseWriteChannel.Op.UpdateSimBalance,
                    Data = new { ModemId = _modemId, Balance = balance.Value, Source = BalanceSource.MeetMob }
                });
                return true;
            }

            if (_meetMob.IsWafBlocked(phone))
            {
                _log.LogWarning("Modem {Id} [{Phone}]: MeetMob balance null but WAF is blocking — skipping re-login (token preserved)", _modemId, phone);
                return false;
            }
            if (_meetMob.WasLastRequestNetworkError(phone))
            {
                _log.LogWarning("Modem {Id} [{Phone}]: MeetMob balance network error — short cooldown (token preserved)", _modemId, phone);
                _meetMob.SetCooldown(phone, 30);
                return false;
            }

            var errorCode = _meetMob.GetLastErrorCode(phone);
            if (string.IsNullOrEmpty(_meetMobToken.AccountId) && errorCode != "MSF.100010" && errorCode != "120")
            {
                _log.LogWarning("Modem {Id} [{Phone}]: MeetMob balance null (accountId empty) — skipping re-login (token preserved)", _modemId, phone);
                return false;
            }

            _log.LogWarning("Modem {Id} [{Phone}]: MeetMob balance returned null (error={Error}) — session may be expired, attempting re-login", _modemId, phone, errorCode ?? "unknown");
            if (!string.IsNullOrEmpty(_meetMobToken.Phone))
                await _meetMob.InvalidateTokenAsync(_meetMobToken.Phone);
            _meetMobToken = null;

            var phoneRaw = await _db.GetPhoneNumberAsync(_imsi);
            if (string.IsNullOrEmpty(phoneRaw))
            {
                _log.LogWarning("Modem {Id} [{Phone}]: No phone number for re-login", _modemId, phone);
                return false;
            }

            var reloginPhone = long.TryParse(phoneRaw, out var phoneLong)
                ? MeetMobService.FormatPhone(phoneLong) ?? phoneRaw
                : phoneRaw;

            MeetMobLoginResult loginResult;
            try
            {
                await _atLock.WaitAsync(ct);
                try { loginResult = await _meetMob.LoginAsync(_imsi, reloginPhone, _at, ct); }
                finally { SafeReleaseAtLock(); }
            }
            catch (OperationCanceledException) { return false; }
            catch (ObjectDisposedException) { return false; }

            if (!loginResult.Success)
            {
                _log.LogWarning("Modem {Id} [{Phone}]: MeetMob re-login failed — {Error}", _modemId, reloginPhone, loginResult.Error);
                _meetMob.SetCooldown(reloginPhone, _config.Get<int>("meetmob.fallback_cooldown", 5));
                return false;
            }

            _meetMobToken = loginResult.Token;
            _lastMeetMobRefreshUtc = DateTime.UtcNow;
            _log.LogInformation("Modem {Id} [{Phone}]: MeetMob re-login success — retrying balance", _modemId, reloginPhone);

            if (_meetMobToken == null)
            {
                _log.LogWarning("Modem {Id} [{Phone}]: MeetMob re-login returned null token", _modemId, reloginPhone);
                return false;
            }

            if (_meetMob.IsWafBlocked(reloginPhone))
            {
                _log.LogWarning("Modem {Id} [{Phone}]: MeetMob WAF blocked after re-login — giving up this cycle", _modemId, reloginPhone);
                return false;
            }

            var retryBalance = await _meetMob.GetBalanceAsync(_meetMobToken, ct);
            if (retryBalance.HasValue)
            {
                _lastBalance = retryBalance.Value;
                _log.LogInformation("Modem {Id} [{Phone}]: Balance = {Balance} DZD (MeetMob re-login)", _modemId, reloginPhone, retryBalance.Value);
                await _db.EnqueueAsync(new()
                {
                    Type = DatabaseWriteChannel.Op.UpdateSimBalance,
                    Data = new { ModemId = _modemId, Balance = retryBalance.Value, Source = BalanceSource.MeetMob }
                });
                return true;
            }

            _log.LogWarning("Modem {Id} [{Phone}]: MeetMob balance still null after re-login", _modemId, reloginPhone);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Modem {Id} [{Phone}]: MeetMob balance timed out (15s) — short cooldown", _modemId, _meetMobPhone);
            _meetMob.SetCooldown(_meetMobPhone, 30);
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var records = await _meetMob.GetRechargeHistoryAsync(_meetMobToken, timeout.Token);
            if (records.Count == 0) return;

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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Modem {Id}: MeetMob history timed out (15s) — short cooldown", _modemId);
            _meetMob?.SetCooldown(_meetMobPhone, 30);
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
            _lastBalance = balance.Value;
            await _db.EnqueueAsync(new() { Type = DatabaseWriteChannel.Op.UpdateSimBalance, Data = new { ModemId = _modemId, Balance = balance.Value } });
        }
        else
        {
            _log.LogWarning("Modem {Id}: *222# returned no balance", _modemId);
        }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        _log.LogDebug("Modem {Id}: Cleanup loop started (hourly)", _modemId);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromHours(1), ct); }
            catch (OperationCanceledException) { break; }

            if (_disposed) break;

            try
            {
                _meetMob?.CleanupStaleEntries();
                var tokenStore = _meetMob?.TokenStore;
                if (tokenStore != null)
                {
                    var purged = await tokenStore.PurgeExpiredAsync();
                    if (purged > 0)
                        _log.LogInformation("Modem {Id}: Purged {Count} expired tokens from disk", _modemId, purged);
                }

                // Invalidate local token if expired
                if (_meetMobToken != null && IsMeetMobTokenExpired())
                {
                    _log.LogDebug("Modem {Id}: MeetMob local token expired — clearing", _modemId);
                    _meetMobToken = null;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _log.LogDebug(ex, "Modem {Id}: Cleanup loop error", _modemId); }
        }
    }

    private async Task NetworkRetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
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
