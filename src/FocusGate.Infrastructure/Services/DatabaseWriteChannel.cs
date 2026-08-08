using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

public class DatabaseWriteChannel
{
    private readonly Channel<WriteOperation> _channel;
    private readonly IServiceProvider _services;
    private readonly ILogger<DatabaseWriteChannel> _logger;
    private Task? _processingTask;
    private readonly ConcurrentDictionary<long, (DateTime At, decimal? RechargeAmount)> _pendingBalanceChecks = new();

    public enum Op
    {
        InsertModem,
        UpdateModemStatus,
        UpdateModemComPort,
        UpsertSimCard,
        UpdateSimCardPhone,
        DeactivateSimCards,
        UpdateSimBalance,
        InsertSms,
        UpdateOrphanedModems,
        CreateWithdrawalRequest,
        ProcessWithdrawal,
        UpdateSimBalanceFromSms,
        TouchModemUpdatedAt,
        CreditUserFromRechargeSms,
        InsertMeetMobHistory
    }

    public DatabaseWriteChannel(IServiceProvider services, ILogger<DatabaseWriteChannel> logger)
    {
        _services = services;
        _logger = logger;
        _channel = Channel.CreateUnbounded<WriteOperation>(new UnboundedChannelOptions { SingleReader = true });
    }

    public void Start(CancellationToken ct) => _processingTask = Task.Run(() => ProcessQueueAsync(ct), ct);
    public ValueTask EnqueueAsync(WriteOperation op) => _channel.Writer.WriteAsync(op);

    public void MarkPendingBalanceCheck(long modemId, decimal? rechargeAmount = null) =>
        _pendingBalanceChecks[modemId] = (DateTime.UtcNow, rechargeAmount);

    public bool TryClaimPendingBalanceCheck(long modemId, out decimal? rechargeAmount)
    {
        CleanupStalePendingBalanceChecks();
        rechargeAmount = null;
        if (!_pendingBalanceChecks.TryRemove(modemId, out var entry)) return false;
        if (DateTime.UtcNow - entry.At >= TimeSpan.FromMinutes(10)) return false;
        rechargeAmount = entry.RechargeAmount;
        return true;
    }

    public void ClearPendingBalanceCheck(long modemId) =>
        _pendingBalanceChecks.TryRemove(modemId, out _);

    private void CleanupStalePendingBalanceChecks()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(15);
        foreach (var kvp in _pendingBalanceChecks)
        {
            if (kvp.Value.At < cutoff)
                _pendingBalanceChecks.TryRemove(kvp.Key, out _);
        }
    }

    public async Task CompleteAsync()
    {
        _channel.Writer.Complete();
        if (_processingTask != null)
        {
            try { await _processingTask; }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
        }
    }

    public async Task<long> GetActiveSimCardIdAsync(int modemId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive);
        return sim?.Id ?? 0;
    }

    public async Task<(string Imsi, long PhoneNumber)> GetActiveSimInfoAsync(int modemId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive);
        return sim != null ? (sim.IMSI, sim.PhoneNumber) : (string.Empty, 0L);
    }

    public async Task<string?> GetPhoneNumberAsync(string imsi)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.IMSI == imsi && s.IsActive && s.PhoneNumber > 0);
        return sim?.PhoneNumber > 0 ? sim.PhoneNumber.ToString() : null;
    }

    public class WriteOperation
    {
        public Op Type { get; set; }
        public object? Data { get; set; }
        public TaskCompletionSource<bool>? Completed { get; set; }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        Action<FocusGateDbContext>? machineSetter = null;
        try
        {
            using var setupScope = _services.CreateScope();
            machineSetter = setupScope.ServiceProvider.GetRequiredService<Action<FocusGateDbContext>>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MachineId setter resolution failed — MachineId will not be stamped on writes");
        }

        await foreach (var op in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
                machineSetter?.Invoke(db);

                bool success = false;
                switch (op.Type)
                {
                    case Op.InsertModem:         await HandleInsertModemAsync(db, op.Data!, ct); success = true; break;
                    case Op.UpdateModemStatus:    await HandleUpdateModemStatusAsync(db, op.Data!, ct); success = true; break;
                    case Op.UpdateModemComPort:   await HandleUpdateModemComPortAsync(db, op.Data!, ct); success = true; break;
                    case Op.UpsertSimCard:        await HandleUpsertSimCardAsync(db, op.Data!, ct); success = true; break;
                    case Op.UpdateSimCardPhone:   await HandleUpdateSimCardPhoneAsync(db, op.Data!, ct); success = true; break;
                    case Op.DeactivateSimCards:   await HandleDeactivateSimCardsAsync(db, op.Data!, ct); success = true; break;
                    case Op.UpdateSimBalance:     success = await HandleUpdateSimBalanceAsync(db, op.Data!, ct); break;
                    case Op.InsertSms:            success = await HandleInsertSmsAsync(db, (SmsRecord)op.Data!, ct); break;
                    case Op.UpdateOrphanedModems: await HandleUpdateOrphanedModemsAsync(db, op.Data!, ct); success = true; break;
                    case Op.CreateWithdrawalRequest: success = await HandleCreateWithdrawalRequestAsync(db, op.Data!, ct); break;
                    case Op.ProcessWithdrawal: success = await HandleProcessWithdrawalAsync(db, op.Data!, ct); break;
                    case Op.UpdateSimBalanceFromSms: success = await HandleUpdateSimBalanceFromSmsAsync(db, op.Data!, ct); break;
                    case Op.TouchModemUpdatedAt:    await HandleTouchModemUpdatedAtAsync(db, op.Data!, ct); success = true; break;
                    case Op.CreditUserFromRechargeSms: success = await HandleCreditUserFromRechargeSmsAsync(db, op.Data!, ct); break;
                    case Op.InsertMeetMobHistory: success = await HandleInsertMeetMobHistoryAsync(db, op.Data!, ct); break;
                }
                op.Completed?.TrySetResult(success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WriteChannel error: {OpType}", op.Type);
                op.Completed?.TrySetResult(false);
            }
        }
    }

    private async Task HandleInsertModemAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var imei = d["IMEI"].GetString() ?? "";
        var imsi = d["IMSI"].GetString() ?? "";
        var phone = d.ContainsKey("PhoneNumber") ? d["PhoneNumber"].GetInt64() : 0;
        var comPort = d.ContainsKey("ComPort") ? d["ComPort"].GetString() ?? "" : "";
        var manufacturer = d.ContainsKey("Manufacturer") ? d["Manufacturer"].GetString() : null;
        var model = d.ContainsKey("Model") ? d["Model"].GetString() : null;
        var brand = d.ContainsKey("Brand") ? (ModemBrand)d["Brand"].GetInt32() : ModemBrand.Unknown;

        if (await db.Modems.AnyAsync(m => m.IMEI == imei, ct))
        {
            _logger.LogDebug("Modem already exists: {IMEI}", imei);
            return;
        }

        var modem = new Modem
        {
            IMEI = imei,
            ComPort = comPort,
            Status = ModemStatus.Detected,
            Brand = brand,
            Manufacturer = manufacturer,
            Model = model,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Modems.Add(modem);

        var sim = new SimCard
        {
            Modem = modem,
            IMSI = imsi,
            PhoneNumber = phone,
            IsActive = true,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        db.SimCards.Add(sim);

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("New modem: Id={Id} IMEI={IMEI} SIM IMSI={IMSI}", modem.Id, imei, imsi);
    }

    private async Task HandleUpdateModemStatusAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var status = (ModemStatus)d["Status"].GetInt32();

        await db.Modems
            .Where(m => m.Id == modemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, status)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);

        if (status == ModemStatus.Offline)
        {
            await db.SimCards
                .Where(s => s.ModemId == modemId && s.IsActive)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(s => s.LastSeen, DateTime.UtcNow), ct);
        }
    }

    private async Task HandleTouchModemUpdatedAtAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();

        await db.Modems
            .Where(m => m.Id == modemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);
    }

    private async Task HandleUpdateModemComPortAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var comPort = d["ComPort"].ValueKind == JsonValueKind.Null ? null : d["ComPort"].GetString();

        await db.Modems
            .Where(m => m.Id == modemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.ComPort, comPort)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);
    }

    private async Task HandleUpsertSimCardAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var imsi = d["IMSI"].GetString() ?? "";

        var activeSim = await db.SimCards
            .FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive, ct);

        if (activeSim != null && activeSim.IMSI == imsi)
        {
            activeSim.LastSeen = DateTime.UtcNow;
            var phone = d.ContainsKey("PhoneNumber") ? d["PhoneNumber"].GetInt64() : 0;
            if (phone > 0)
                activeSim.PhoneNumber = phone;
            await db.SaveChangesAsync(ct);
            return;
        }

        if (activeSim != null)
        {
            activeSim.IsActive = false;
            activeSim.RemovedAt = DateTime.UtcNow;
            activeSim.ReplacedAt = DateTime.UtcNow;
            activeSim.LastSeen = DateTime.UtcNow;
        }

        var newPhone = d.ContainsKey("PhoneNumber") ? d["PhoneNumber"].GetInt64() : 0;
        var newSim = new SimCard
        {
            ModemId = modemId,
            IMSI = imsi,
            PhoneNumber = newPhone,
            IsActive = true,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        db.SimCards.Add(newSim);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("SIM changed: ModemId={Id} IMSI={IMSI}", modemId, imsi);
    }

    private async Task HandleUpdateSimCardPhoneAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var phone = d["PhoneNumber"].GetInt64();

        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive, ct);
        if (sim == null)
        {
            _logger.LogWarning("UpdateSimCardPhone: No active SIM on modem {ModemId} — phone {Phone} not saved", modemId, phone);
            return;
        }
        if (phone > 0)
        {
            sim.PhoneNumber = phone;
            sim.LastSeen = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task HandleDeactivateSimCardsAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var now = DateTime.UtcNow;

        await db.SimCards
            .Where(s => s.ModemId == modemId && s.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsActive, false)
                .SetProperty(x => x.RemovedAt, now)
                .SetProperty(x => x.LastSeen, now), ct);
    }

    private async Task<bool> HandleUpdateSimBalanceAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var newBalance = d["Balance"].GetDecimal();
        var source = BalanceSource.USSD;
        if (d.TryGetValue("Source", out var srcElem))
        {
            if (srcElem.ValueKind == JsonValueKind.String)
                Enum.TryParse<BalanceSource>(srcElem.GetString(), true, out source);
            else if (srcElem.ValueKind == JsonValueKind.Number)
                source = (BalanceSource)srcElem.GetInt32();
        }

        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive, ct);
        if (sim != null)
        {
            var oldBalance = sim.Balance;
            sim.Balance = newBalance;
            sim.VerifiedAt = DateTime.UtcNow;
            sim.LastSeen = DateTime.UtcNow;

            var modem = await db.Modems.FirstOrDefaultAsync(m => m.Id == modemId, ct);
            if (modem != null)
                modem.UpdatedAt = DateTime.UtcNow;

            if (newBalance > oldBalance)
            {
                var userId = await ModemHelper.ResolveUserIdForModemAsync(db, modemId, ct);

                db.BalanceHistories.Add(new BalanceHistory
                {
                    SimCardId = sim.Id,
                    ModemId = modemId,
                    UserId = userId,
                    Balance = newBalance,
                    PreviousBalance = oldBalance,
                    Source = source,
                    RecordedAt = DateTime.UtcNow
                });

                _logger.LogInformation("{Source} balance recorded: Modem={ModemId} Sim={SimId} {Old:F2} → {New:F2} DZD (user credit via recharge SMS)",
                    source, modemId, sim.Id, oldBalance, newBalance);
            }
            else if (newBalance < oldBalance)
            {
                _logger.LogInformation("Balance decreased via {Source}: Modem={ModemId} Sim={SimId} {Old:F2} → {New:F2} DZD",
                    source, modemId, sim.Id, oldBalance, newBalance);
            }

            await db.SaveChangesAsync(ct);
            return true;
        }
        _logger.LogWarning("{Source} balance: No active SIM on modem {ModemId}", source, modemId);
        return false;
    }

    private async Task<bool> HandleUpdateSimBalanceFromSmsAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var newBalance = d["Balance"].GetDecimal();

        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive, ct);
        if (sim == null) return false;

        var oldSimBalance = sim.Balance;
        sim.Balance = newBalance;
        sim.VerifiedAt = DateTime.UtcNow;
        sim.LastSeen = DateTime.UtcNow;

        if (newBalance > oldSimBalance)
        {
            var userId = await ModemHelper.ResolveUserIdForModemAsync(db, modemId, ct);

            db.BalanceHistories.Add(new BalanceHistory
            {
                SimCardId = sim.Id,
                ModemId = modemId,
                UserId = userId,
                Balance = newBalance,
                PreviousBalance = oldSimBalance,
                Source = BalanceSource.SMS,
                RecordedAt = DateTime.UtcNow
            });

            _logger.LogInformation("Balance recorded via *222# after recharge: Modem={ModemId} Sim={SimId} {Old:F2} → {New:F2} DZD (user credit via recharge SMS)",
                modemId, sim.Id, oldSimBalance, newBalance);
        }
        else if (newBalance < oldSimBalance)
        {
            _logger.LogInformation("Balance decreased via *222# SMS: Modem={ModemId} Sim={SimId} {Old:F2} → {New:F2} DZD",
                modemId, sim.Id, oldSimBalance, newBalance);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> HandleCreditUserFromRechargeSmsAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var rechargeAmount = d["RechargeAmount"].GetDecimal();

        if (rechargeAmount <= 0) return false;

        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive, ct);
        if (sim == null) return false;

        var userId = await ModemHelper.ResolveUserIdForModemAsync(db, modemId, ct);
        if (userId <= 0)
        {
            _logger.LogWarning("CREDIT ORPHANED via recharge SMS: Modem={ModemId} Sim={SimId} +{Amount:F2} DZD — no user assigned", modemId, sim.Id, rechargeAmount);
            return false;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        var alreadyCredited = await db.UserBalanceHistories.AnyAsync(h =>
            h.UserId == userId
            && h.SimCardId == sim.Id
            && h.Amount == rechargeAmount
            && h.Type == 0
            && h.RecordedAt >= cutoff, ct);

        if (alreadyCredited)
        {
            _logger.LogInformation("CREDIT SKIPPED (duplicate): Modem={ModemId} Sim={SimId} User={UserId} +{Amount:F2} DZD — already credited within 30min", modemId, sim.Id, userId, rechargeAmount);
            return false;
        }

        // Credit user wallet only — SIM balance is updated separately by MeetMob/USSD snapshot.
        // Do NOT modify sim.Balance here to avoid double-counting when a balance snapshot already ran.
        var (credited, userOld, userNew) = CreditUserBalance(db, userId, rechargeAmount, sim.Id);
        if (credited)
            _logger.LogInformation("CREDIT via recharge SMS: Modem={ModemId} Sim={SimId} User={UserId} +{Amount:F2} DZD, Wallet: {UserOld:F2} → {UserNew:F2}",
                modemId, sim.Id, userId, rechargeAmount, userOld, userNew);
        else
            _logger.LogWarning("CREDIT FAILED via recharge SMS: Modem={ModemId} Sim={SimId} +{Amount:F2} DZD — user {UserId} not found", modemId, sim.Id, rechargeAmount, userId);

        await db.SaveChangesAsync(ct);
        return credited;
    }

    private async Task<bool> HandleInsertSmsAsync(FocusGateDbContext db, SmsRecord sms, CancellationToken ct)
    {
        if (sms.SimCardId <= 0)
        {
            _logger.LogWarning("SMS has no SimCardId, discarding: Sender={Sender}", sms.SenderNumber);
            return false;
        }

        // Skip re-reads of the same SMS from the SIM (same sender + content + same day)
        // Same text from same SIM on the same day is a duplicate re-read from SIM memory
        var exists = await db.SmsRecords
            .AnyAsync(s => s.SimCardId == sms.SimCardId
                && s.SenderNumber == sms.SenderNumber
                && s.Content == sms.Content
                && s.ReceivedAt >= sms.ReceivedAt.AddHours(-24)
                && s.ReceivedAt <= sms.ReceivedAt.AddHours(24), ct);

        if (exists)
        {
            _logger.LogDebug("SMS duplicate skipped: SimCardId={SimId} Sender={Sender}", sms.SimCardId, sms.SenderNumber);
            return false;
        }

        db.SmsRecords.Add(sms);

        var smsType = ClassifySmsType(sms.SenderNumber, sms.Content ?? "");
        _logger.LogInformation("SMS saved: Sim={SimId} Sender={Sender} Type={Type} Content={Content}",
            sms.SimCardId, sms.SenderNumber, smsType, (sms.Content ?? "").Substring(0, Math.Min(80, sms.Content?.Length ?? 0)));

        var isMobilisSms = sms.SenderNumber?.Trim() is "Mobilis" or "77111" or "610";
        if (isMobilisSms)
        {
            var sim = await db.SimCards.FirstOrDefaultAsync(s => s.Id == sms.SimCardId && s.IsActive, ct);
            if (sim == null) isMobilisSms = false;
            else await ProcessMobilisSmsAsync(db, sim, sms, ct);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task ProcessMobilisSmsAsync(FocusGateDbContext db, SimCard sim, SmsRecord sms, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        sim.VerifiedAt = now;
        sim.LastSeen = now;

        if (sms.Content.Contains("Solde", StringComparison.OrdinalIgnoreCase))
        {
            var balance = ExtractBalanceFromContent(sms.Content);
            if (balance.HasValue)
            {
                var oldSimBalance = sim.Balance;
                sim.Balance = balance.Value;
                sim.VerifiedAt = now;
                sim.LastSeen = now;

                long? userId = null;
                if (oldSimBalance != balance.Value)
                {
                    userId = await ModemHelper.ResolveUserIdForModemAsync(db, sim.ModemId, ct);

                    db.BalanceHistories.Add(new BalanceHistory
                    {
                        SimCardId = sim.Id,
                        ModemId = sim.ModemId,
                        UserId = userId,
                        Balance = balance.Value,
                        PreviousBalance = oldSimBalance,
                        Source = BalanceSource.SMS,
                        RecordedAt = now
                    });

                    _logger.LogInformation("SIM Balance updated from Solde SMS: Sim={SimId} Modem={ModemId} {Old:F2} → {New:F2} DZD",
                        sim.Id, sim.ModemId, oldSimBalance, balance.Value);
                }

                // If a pending balance check was set (from a recharge SMS where balance was unavailable),
                // credit the user with the stored exact recharge amount.
                if (TryClaimPendingBalanceCheck(sim.ModemId, out var pendingRechargeAmount))
                {
                    if (pendingRechargeAmount.HasValue && pendingRechargeAmount.Value > 0)
                    {
                        userId ??= await ModemHelper.ResolveUserIdForModemAsync(db, sim.ModemId, ct);
                        if (userId.HasValue && userId.Value > 0)
                        {
                            var (credited, userOld, userNew) = CreditUserBalance(db, userId.Value, pendingRechargeAmount.Value, sim.Id);
                            if (credited)
                                _logger.LogInformation("CREDIT via Solde SMS (pending): Modem={ModemId} Sim={SimId} User={UserId} +{Amount:F2} DZD, Wallet: {Old:F2} → {New:F2}",
                                    sim.ModemId, sim.Id, userId.Value, pendingRechargeAmount.Value, userOld, userNew);
                            else
                                _logger.LogWarning("CREDIT FAILED via Solde SMS (pending): Modem={ModemId} Sim={SimId} +{Amount:F2} DZD — user not found",
                                    sim.ModemId, sim.Id, pendingRechargeAmount.Value);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Solde SMS claimed pending flag but no recharge amount stored — skipping user credit: Modem={ModemId} Sim={SimId}",
                            sim.ModemId, sim.Id);
                    }
                }
            }
        }
        else if (IsRechargeSms(sms.Content))
        {
            var rechargeAmount = ExtractRechargeAmountFromContent(sms.Content);
            if (rechargeAmount.HasValue && rechargeAmount.Value > 0)
            {
                var userId = await ModemHelper.ResolveUserIdForModemAsync(db, sim.ModemId, ct);
                if (userId.HasValue && userId.Value > 0)
                {
                    var cutoff = DateTime.UtcNow.AddSeconds(-15);
                    var alreadyCredited = await db.UserBalanceHistories.AnyAsync(h =>
                        h.UserId == userId.Value
                        && h.SimCardId == sim.Id
                        && h.Amount == rechargeAmount.Value
                        && h.Type == 0
                        && h.RecordedAt >= cutoff, ct);

                    if (!alreadyCredited)
                    {
                        var (credited, userOld, userNew) = CreditUserBalance(db, userId.Value, rechargeAmount.Value, sim.Id);
                        if (credited)
                        {
                            _logger.LogInformation("INSTANT CREDIT via Recharge SMS: Modem={ModemId} Sim={SimId} User={UserId} +{Amount:F2} DZD, Wallet: {UserOld:F2} → {UserNew:F2}",
                                sim.ModemId, sim.Id, userId.Value, rechargeAmount.Value, userOld, userNew);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Recharge SMS already credited within 30min: Modem={ModemId} Sim={SimId} User={UserId} Amount={Amount:F2} DZD",
                            sim.ModemId, sim.Id, userId.Value, rechargeAmount.Value);
                    }
                }
                else
                {
                    _logger.LogWarning("Recharge SMS received but no user assigned to Modem={ModemId}: Sim={SimId} +{Amount:F2} DZD",
                        sim.ModemId, sim.Id, rechargeAmount.Value);
                }
            }
        }
    }

    internal static decimal? ExtractBalanceFromContent(string content)
    {
        var soldeIdx = content.IndexOf("Solde", StringComparison.OrdinalIgnoreCase);
        if (soldeIdx < 0) return null;

        var afterSolde = content[(soldeIdx + 5)..];
        var numMatch = System.Text.RegularExpressions.Regex.Match(afterSolde, @"(\d[\d.,]+)");
        if (!numMatch.Success) return null;

        var numStr = numMatch.Groups[1].Value;
        if (numStr.Contains(',') && numStr.Contains('.'))
        {
            var lastComma = numStr.LastIndexOf(',');
            var lastDot = numStr.LastIndexOf('.');
            if (lastComma > lastDot)
                numStr = numStr.Replace(".", "").Replace(",", ".");
            else
                numStr = numStr.Replace(",", "");
        }
        else if (numStr.Contains(','))
        {
            numStr = numStr.Replace(",", ".");
        }

        if (decimal.TryParse(numStr, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var val))
            return val;
        return null;
    }

    internal static bool IsRechargeSms(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        if (content.Contains("montant de", StringComparison.OrdinalIgnoreCase)
            && content.Contains("reçu", StringComparison.OrdinalIgnoreCase))
            return true;

        if (content.Contains("rechargé", StringComparison.OrdinalIgnoreCase))
            return true;

        if (content.Contains("transféré", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    internal static string ClassifySmsType(string sender, string content)
    {
        if (sender != "Mobilis" && sender != "77111" && sender != "610") return "other";
        if (content.Contains("Solde", StringComparison.OrdinalIgnoreCase)) return "balance";
        if (content.Contains("montant de", StringComparison.OrdinalIgnoreCase)
            && content.Contains("reçu", StringComparison.OrdinalIgnoreCase)) return "transfer";
        if (content.Contains("rechargé", StringComparison.OrdinalIgnoreCase)) return "recharge";
        if (content.Contains("transféré", StringComparison.OrdinalIgnoreCase)) return "transfer";
        if (content.Contains("Votre offre", StringComparison.OrdinalIgnoreCase)
            || content.Contains("offre", StringComparison.OrdinalIgnoreCase)) return "offer";
        return "mobilis-other";
    }

    internal static decimal? ExtractRechargeAmountFromContent(string content)
    {
        var match = System.Text.RegularExpressions.Regex.Match(content, @"montant\s+de\s*(?:un\s+)?(\d[\d.,]*)", RegexOptions.IgnoreCase);
        if (match.Success)
            return ParseAmount(match.Groups[1].Value);

        match = System.Text.RegularExpressions.Regex.Match(content, @"rechargé\s+(?:de\s+)?(\d[\d.,]*)", RegexOptions.IgnoreCase);
        if (match.Success)
            return ParseAmount(match.Groups[1].Value);

        match = System.Text.RegularExpressions.Regex.Match(content, @"transféré\s+(?:un\s+credit\s+de\s+)?(\d[\d.,]*)", RegexOptions.IgnoreCase);
        if (match.Success)
            return ParseAmount(match.Groups[1].Value);

        match = System.Text.RegularExpressions.Regex.Match(content, @"(\d[\d.,]+)\s*(?:DZD|DA)", RegexOptions.IgnoreCase);
        if (match.Success)
            return ParseAmount(match.Groups[1].Value);

        return null;
    }

    internal static decimal? ParseAmount(string numStr)
    {
        if (numStr.Contains(',') && numStr.Contains('.'))
        {
            var lastComma = numStr.LastIndexOf(',');
            var lastDot = numStr.LastIndexOf('.');
            if (lastComma > lastDot)
                numStr = numStr.Replace(".", "").Replace(",", ".");
            else
                numStr = numStr.Replace(",", "");
        }
        else if (numStr.Contains(','))
        {
            numStr = numStr.Replace(",", ".");
        }
        else if (numStr.Contains('.'))
        {
            var lastDot = numStr.LastIndexOf('.');
            var afterDot = numStr[(lastDot + 1)..];
            if (afterDot.Length == 3)
                numStr = numStr.Replace(".", "");
        }

        if (decimal.TryParse(numStr, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var val))
            return val;
        return null;
    }

    private async Task HandleUpdateOrphanedModemsAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var activeImeis = d["ActiveImeis"].EnumerateArray()
            .Select(x => x.GetString() ?? "").ToHashSet();

        if (activeImeis.Count == 0)
        {
            int allOrphaned = await db.Modems
                .Where(m => m.Status != ModemStatus.Offline && m.Status != ModemStatus.Error
                         && m.Status != ModemStatus.Detected && m.Status != ModemStatus.PendingNetwork)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, ModemStatus.Offline)
                    .SetProperty(m => m.ComPort, (string?)null)
                    .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);

            if (allOrphaned > 0)
                _logger.LogWarning("All {Count} online modems orphaned -> Offline", allOrphaned);
            return;
        }

        var orphaned = await db.Modems
            .Where(m => m.Status != ModemStatus.Offline && m.Status != ModemStatus.Error
                     && m.Status != ModemStatus.Detected && m.Status != ModemStatus.PendingNetwork
                     && !activeImeis.Contains(m.IMEI))
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, ModemStatus.Offline)
                .SetProperty(m => m.ComPort, (string?)null)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);

        if (orphaned > 0)
        {
            _logger.LogWarning("{Count} modems orphaned -> Offline (active: {ActiveCount})",
                orphaned, activeImeis.Count);
        }
    }

    private async Task<bool> HandleCreateWithdrawalRequestAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var userId = d["UserId"].GetInt64();
        var amount = d["Amount"].GetDecimal();
        var note = d.ContainsKey("Note") ? d["Note"].GetString() : null;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null) { _logger.LogWarning("Withdrawal request failed: user {UserId} not found", userId); return false; }

        var available = user.Balance;
        if (amount <= 0 || amount > available)
        {
            _logger.LogWarning("Withdrawal request failed: amount {Amount} exceeds available {Available}", amount, available);
            return false;
        }

        var request = new WithdrawalRequest
        {
            UserId = userId,
            Amount = amount,
            Status = WithdrawalStatus.Pending,
            Note = note,
            RequestedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        db.WithdrawalRequests.Add(request);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Withdrawal request created: Id={Id} UserId={UserId} Amount={Amount} DZD", request.Id, userId, amount);
        return true;
    }

    private async Task<bool> HandleProcessWithdrawalAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var requestId = d["RequestId"].GetInt64();
        var adminId = d["AdminId"].GetInt64();
        var approved = d["Approved"].GetBoolean();
        var adminNote = d.ContainsKey("AdminNote") ? d["AdminNote"].GetString() : null;

        var request = await db.WithdrawalRequests.FirstOrDefaultAsync(w => w.Id == requestId, ct);
        if (request == null) { _logger.LogWarning("Process withdrawal failed: request {RequestId} not found", requestId); return false; }

        if (request.Status != WithdrawalStatus.Pending)
        {
            _logger.LogWarning("Process withdrawal failed: request {RequestId} already {Status}", requestId, request.Status);
            return false;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user == null) { _logger.LogWarning("Process withdrawal failed: user not found"); return false; }

        if (approved)
        {
            request.Status = WithdrawalStatus.Approved;
            request.ProcessedByAdminId = adminId > 0 ? adminId : null;
            request.ProcessedAt = DateTime.UtcNow;
            request.AdminNote = adminNote;

            var oldBalance = user.Balance;
            user.Balance = Math.Max(0, user.Balance - request.Amount);

            db.BalanceHistories.Add(new BalanceHistory
            {
                SimCardId = null,
                ModemId = null,
                UserId = user.Id,
                Balance = user.Balance,
                PreviousBalance = oldBalance,
                Source = BalanceSource.Withdrawal,
                RecordedAt = DateTime.UtcNow
            });

            db.UserBalanceHistories.Add(new UserBalanceHistory
            {
                UserId = user.Id,
                Amount = -request.Amount,
                BalanceAfter = user.Balance,
                Type = 1,
                SimCardId = null,
                Note = $"Withdrawal approved{(string.IsNullOrEmpty(adminNote) ? "" : ": " + adminNote)}",
                RecordedAt = DateTime.UtcNow
            });

            _logger.LogInformation("Withdrawal approved: Request={RequestId} User={UserId} Amount={Amount} DZD", requestId, user.Id, request.Amount);
        }
        else
        {
            request.Status = WithdrawalStatus.Rejected;
            request.ProcessedByAdminId = adminId > 0 ? adminId : null;
            request.ProcessedAt = DateTime.UtcNow;
            request.AdminNote = adminNote;

            _logger.LogInformation("Withdrawal rejected: Request={RequestId} User={UserId}", requestId, user.Id);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static (bool credited, decimal oldBalance, decimal newBalance) CreditUserBalance(FocusGateDbContext db, long? userId, decimal amount, long? simCardId)
    {
        if (amount <= 0 || !userId.HasValue) return (false, 0, 0);

        var user = db.Users.FirstOrDefault(u => u.Id == userId.Value);
        if (user == null) return (false, 0, 0);

        var oldBalance = user.Balance;
        user.Balance += amount;

        db.UserBalanceHistories.Add(new UserBalanceHistory
        {
            UserId = userId.Value,
            Amount = amount,
            BalanceAfter = user.Balance,
            Type = 0,
            SimCardId = simCardId,
            Note = "Credit from SIM",
            RecordedAt = DateTime.UtcNow
        });
        return (true, oldBalance, user.Balance);
    }

    private async Task<bool> HandleInsertMeetMobHistoryAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var imsi = d["Imsi"].GetString() ?? "";
        var records = JsonSerializer.Deserialize<List<MeetMobRechargeRecordDto>>(d["Records"].GetRawText()) ?? new();

        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive, ct);
        if (sim == null)
        {
            _logger.LogWarning("MeetMob history: No active SIM on modem {ModemId}", modemId);
            return false;
        }

        var userId = await ModemHelper.ResolveUserIdForModemAsync(db, modemId, ct);
        var inserted = 0;

        foreach (var record in records)
        {
            if (!DateTime.TryParse(record.TradeTime, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var recordedAt))
            {
                _logger.LogDebug("MeetMob history: Failed to parse timestamp '{Time}' for modem {ModemId}", record.TradeTime, modemId);
                continue;
            }

            if (!decimal.TryParse(MeetMobService.NormalizeMeetMobAmount(record.Amount),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            {
                _logger.LogDebug("MeetMob history: Failed to parse amount '{Amount}' for modem {ModemId}", record.Amount, modemId);
                continue;
            }

            var oldBalance = sim.Balance;
            sim.Balance = amount;
            sim.VerifiedAt = DateTime.UtcNow;
            sim.LastSeen = DateTime.UtcNow;

            db.BalanceHistories.Add(new BalanceHistory
            {
                SimCardId = sim.Id,
                ModemId = modemId,
                UserId = userId > 0 ? userId : null,
                Balance = amount,
                PreviousBalance = oldBalance,
                Source = BalanceSource.MeetMob,
                RecordedAt = recordedAt
            });
            inserted++;
        }

        if (inserted > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("MeetMob history: Inserted {Count} records for modem {ModemId} SIM {SimId}", inserted, modemId, sim.Id);
        }

        return inserted > 0;
    }

    private static async Task<bool> IsDuplicateBalanceHistoryAsync(FocusGateDbContext db, long? simCardId, BalanceSource source, DateTime recordedAt, decimal amount)
    {
        if (simCardId == null) return false;
        var window = TimeSpan.FromSeconds(5);
        return await db.BalanceHistories.AnyAsync(h =>
            h.SimCardId == simCardId &&
            h.Source == source &&
            h.Balance == amount &&
            h.RecordedAt >= recordedAt - window &&
            h.RecordedAt <= recordedAt + window);
    }

    private static Dictionary<string, JsonElement> Deserialize(object data)
    {
        var json = JsonSerializer.Serialize(data);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
    }
}

public class MeetMobRechargeRecordDto
{
    public string TradeTime { get; set; } = string.Empty;
    public string Amount { get; set; } = "0";
}
