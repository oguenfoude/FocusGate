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
    private readonly ConcurrentDictionary<long, DateTime> _pendingBalanceChecks = new();

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
        TouchModemUpdatedAt
    }

    public DatabaseWriteChannel(IServiceProvider services, ILogger<DatabaseWriteChannel> logger)
    {
        _services = services;
        _logger = logger;
        _channel = Channel.CreateUnbounded<WriteOperation>(new UnboundedChannelOptions { SingleReader = true });
    }

    public void Start(CancellationToken ct) => _processingTask = Task.Run(() => ProcessQueueAsync(ct), ct);
    public ValueTask EnqueueAsync(WriteOperation op) => _channel.Writer.WriteAsync(op);

    public void MarkPendingBalanceCheck(long modemId) =>
        _pendingBalanceChecks[modemId] = DateTime.UtcNow;

    public bool TryClaimPendingBalanceCheck(long modemId) =>
        _pendingBalanceChecks.TryRemove(modemId, out var pendingAt)
        && DateTime.UtcNow - pendingAt < TimeSpan.FromMinutes(2);

    public void ClearPendingBalanceCheck(long modemId) =>
        _pendingBalanceChecks.TryRemove(modemId, out _);

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
        await db.SaveChangesAsync(ct);

        var sim = new SimCard
        {
            ModemId = modem.Id,
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

        var active = await db.SimCards.Where(s => s.ModemId == modemId && s.IsActive).ToListAsync(ct);
        foreach (var sim in active)
        {
            sim.IsActive = false;
            sim.RemovedAt = DateTime.UtcNow;
            sim.LastSeen = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> HandleUpdateSimBalanceAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var newBalance = d["Balance"].GetDecimal();

        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive, ct);
        if (sim != null)
        {
            var oldBalance = sim.Balance;
            sim.Balance = newBalance;
            sim.VerifiedAt = DateTime.UtcNow;
            sim.LastSeen = DateTime.UtcNow;

            await db.Modems
                .Where(m => m.Id == modemId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);

            var userId = await ModemHelper.ResolveUserIdForModemAsync(db, modemId, ct);
            
            if (oldBalance != newBalance)
            {
                db.BalanceHistories.Add(new BalanceHistory
                {
                    SimCardId = sim.Id,
                    ModemId = modemId,
                    UserId = userId,
                    Balance = newBalance,
                    PreviousBalance = oldBalance,
                    Source = BalanceSource.USSD,
                    RecordedAt = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync(ct);
            return true;
        }
        return false;
    }

    private async Task<bool> HandleUpdateSimBalanceFromSmsAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var newBalance = d["Balance"].GetDecimal();

        ClearPendingBalanceCheck(modemId);

        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive, ct);
        if (sim == null) return false;

        var oldSimBalance = sim.Balance;
        sim.Balance = newBalance;
        sim.VerifiedAt = DateTime.UtcNow;
        sim.LastSeen = DateTime.UtcNow;

        var userId = await ModemHelper.ResolveUserIdForModemAsync(db, modemId, ct);

        if (oldSimBalance != newBalance)
        {
            db.BalanceHistories.Add(new BalanceHistory
            {
                SimCardId = sim.Id,
                ModemId = modemId,
                UserId = userId,
                Balance = newBalance,
                PreviousBalance = oldSimBalance,
                Source = BalanceSource.USSD,
                RecordedAt = DateTime.UtcNow
            });

            if (userId > 0 && newBalance > oldSimBalance)
            {
                var delta = newBalance - oldSimBalance;
                CreditUserBalance(db, userId, delta, sim.Id);
                _logger.LogInformation("Balance increased via *222#: Modem={Id} Old={Old:F2} → New={New:F2}, User credited +{Delta:F2} DZD", modemId, oldSimBalance, newBalance, delta);
            }
            else if (newBalance < oldSimBalance)
            {
                _logger.LogInformation("Balance decreased via *222# (offer/deduction): Modem={Id} Old={Old:F2} → New={New:F2}", modemId, oldSimBalance, newBalance);
            }
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> HandleInsertSmsAsync(FocusGateDbContext db, SmsRecord sms, CancellationToken ct)
    {
        if (sms.SimCardId <= 0)
        {
            _logger.LogWarning("SMS has no SimCardId, discarding: Sender={Sender}", sms.SenderNumber);
            return false;
        }

        // Skip re-reads of the same SMS from the SIM (same sender + content + time)
        // Same text at a different time is a new message — always store it
        var exists = await db.SmsRecords
            .AnyAsync(s => s.SimCardId == sms.SimCardId
                && s.SenderNumber == sms.SenderNumber
                && s.Content == sms.Content
                && s.ReceivedAt == sms.ReceivedAt, ct);

        if (exists)
        {
            _logger.LogDebug("SMS duplicate skipped: SimCardId={SimId} Sender={Sender}", sms.SimCardId, sms.SenderNumber);
            return false;
        }

        db.SmsRecords.Add(sms);
        await db.SaveChangesAsync(ct);

        if (sms.SenderNumber != "Mobilis" && sms.SenderNumber != "77111" && sms.SenderNumber != "610") return true;

        var sim = await db.SimCards.FindAsync(new object[] { sms.SimCardId }, ct);
        if (sim == null) return true;

        sim.VerifiedAt = DateTime.UtcNow;
        sim.LastSeen = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (sms.Content.Contains("Solde", StringComparison.OrdinalIgnoreCase))
        {
            var balance = ExtractBalanceFromContent(sms.Content);
            if (balance.HasValue)
            {
                var oldSimBalance = sim.Balance;
                sim.Balance = balance.Value;
                sim.VerifiedAt = DateTime.UtcNow;
                sim.LastSeen = DateTime.UtcNow;

                if (oldSimBalance != balance.Value)
                {
                    var userId = await ModemHelper.ResolveUserIdForModemAsync(db, sim.ModemId, ct);

                    db.BalanceHistories.Add(new BalanceHistory
                    {
                        SimCardId = sim.Id,
                        ModemId = sim.ModemId,
                        UserId = userId,
                        Balance = balance.Value,
                        PreviousBalance = oldSimBalance,
                        Source = BalanceSource.SMS,
                        RecordedAt = DateTime.UtcNow
                    });

                    if (userId > 0 && balance.Value > oldSimBalance)
                    {
                        var isRechargeTransfer = sms.Content.Contains("montant de", StringComparison.OrdinalIgnoreCase)
                            && sms.Content.Contains("reçu", StringComparison.OrdinalIgnoreCase);

                        if (isRechargeTransfer || TryClaimPendingBalanceCheck(sim.ModemId))
                        {
                            var delta = balance.Value - oldSimBalance;
                            CreditUserBalance(db, userId, delta, sim.Id);
                            _logger.LogInformation("Balance increased via Solde SMS: Sim={SimId} Old={Old:F2} → New={New:F2}, User credited +{Delta:F2} DZD (recharge={IsRecharge})", sim.Id, oldSimBalance, balance.Value, delta, isRechargeTransfer);
                        }
                        else
                        {
                            _logger.LogDebug("Balance increased via Solde SMS but no pending check and not recharge: Sim={SimId}", sim.Id);
                        }
                    }
                    else if (balance.Value < oldSimBalance)
                    {
                        _logger.LogDebug("Balance decreased via Solde SMS: Sim={SimId} Old={Old:F2} → New={New:F2}", sim.Id, oldSimBalance, balance.Value);
                    }
                }

                await db.SaveChangesAsync(ct);
            }
        }
        else if (IsRechargeSms(sms.Content))
        {
            var rechargeAmount = ExtractRechargeAmountFromContent(sms.Content);
            if (rechargeAmount.HasValue && rechargeAmount.Value > 0 && rechargeAmount.Value < 50000)
            {
                var oldSimBalance = sim.Balance;
                sim.Balance += rechargeAmount.Value;
                sim.VerifiedAt = DateTime.UtcNow;
                sim.LastSeen = DateTime.UtcNow;

                var userId = await ModemHelper.ResolveUserIdForModemAsync(db, sim.ModemId, ct);

                db.BalanceHistories.Add(new BalanceHistory
                {
                    SimCardId = sim.Id,
                    ModemId = sim.ModemId,
                    UserId = userId,
                    Balance = sim.Balance,
                    PreviousBalance = oldSimBalance,
                    Source = BalanceSource.SMS,
                    RecordedAt = DateTime.UtcNow
                });

                if (userId > 0)
                {
                    CreditUserBalance(db, userId, rechargeAmount.Value, sim.Id);
                    _logger.LogInformation("Balance credited via recharge SMS: Sim={SimId} Amount={Amount:F2}, New Balance={New:F2}", sim.Id, rechargeAmount.Value, sim.Balance);
                }

                await db.SaveChangesAsync(ct);
            }
        }

        return true;
    }

    private static decimal? ExtractBalanceFromContent(string content)
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

    private static bool IsRechargeSms(string content)
    {
        return content.Contains("montant de", StringComparison.OrdinalIgnoreCase)
            && content.Contains("reçu", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? ExtractRechargeAmountFromContent(string content)
    {
        var match = System.Text.RegularExpressions.Regex.Match(content, @"montant\s+de\s*(\d[\d.,]*)", RegexOptions.IgnoreCase);
        if (match.Success)
            return ParseAmount(match.Groups[1].Value);

        match = System.Text.RegularExpressions.Regex.Match(content, @"rechargé\s*(\d[\d.,]*)", RegexOptions.IgnoreCase);
        if (match.Success)
            return ParseAmount(match.Groups[1].Value);

        return null;
    }

    private static decimal? ParseAmount(string numStr)
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

        var online = await db.Modems
            .Where(m => m.Status != ModemStatus.Offline && m.Status != ModemStatus.Error && m.Status != ModemStatus.Detected && m.Status != ModemStatus.PendingNetwork)
            .ToListAsync(ct);

        var orphaned = 0;
        foreach (var modem in online)
        {
            if (!activeImeis.Contains(modem.IMEI))
            {
                _logger.LogWarning("Modem {Id} ({IMEI}) orphaned -> Offline (active: {ActiveCount}, checked: {CheckedCount})",
                    modem.Id, modem.IMEI, activeImeis.Count, online.Count);
                modem.Status = ModemStatus.Offline;
                modem.ComPort = null;
                modem.UpdatedAt = DateTime.UtcNow;
                orphaned++;
            }
        }
        if (online.Count > 0)
            _logger.LogDebug("Orphan check: {Orphaned}/{Total} modems orphaned (active IMEIs: {ActiveCount})", orphaned, online.Count, activeImeis.Count);
        await db.SaveChangesAsync(ct);
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
            request.ProcessedByAdminId = adminId;
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
            request.ProcessedByAdminId = adminId;
            request.ProcessedAt = DateTime.UtcNow;
            request.AdminNote = adminNote;

            _logger.LogInformation("Withdrawal rejected: Request={RequestId} User={UserId}", requestId, user.Id);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static void CreditUserBalance(FocusGateDbContext db, long? userId, decimal amount, long? simCardId)
    {
        if (amount <= 0 || !userId.HasValue) return;

        var user = db.Users.FirstOrDefault(u => u.Id == userId.Value);
        if (user == null) return;

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
    }

    private static Dictionary<string, JsonElement> Deserialize(object data)
    {
        var json = JsonSerializer.Serialize(data);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
    }
}
