using System.Globalization;
using System.Text.Json;
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
        ProcessWithdrawal
    }

    public DatabaseWriteChannel(IServiceProvider services, ILogger<DatabaseWriteChannel> logger)
    {
        _services = services;
        _logger = logger;
        _channel = Channel.CreateUnbounded<WriteOperation>(new UnboundedChannelOptions { SingleReader = true });
    }

    public void Start(CancellationToken ct) => _processingTask = Task.Run(() => ProcessQueueAsync(ct), ct);
    public ValueTask EnqueueAsync(WriteOperation op) => _channel.Writer.WriteAsync(op);

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

    public async Task<string> GetActiveImsiAsync(int modemId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive);
        return sim?.IMSI ?? string.Empty;
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
        await foreach (var op in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

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
                }
                op.Completed?.TrySetResult(success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WriteChannel error: {OpType}", op.Type);
                op.Completed?.TrySetException(ex);
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

        _logger.LogInformation("Modem {Id} -> {Status}", modemId, status);
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
        if (sim != null && phone > 0)
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

            await db.SaveChangesAsync(ct);

            var userId = await ResolveUserIdForModemAsync(db, modemId, ct);
            
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
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Balance changed: Modem={Id} {Old:F2} DZD → {New:F2} DZD", modemId, oldBalance, newBalance);
            }
            else
            {
                _logger.LogInformation("Balance confirmed: Modem={Id} {Balance:F2} DZD (no change)", modemId, newBalance);
            }
            return true;
        }
        return false;
    }

    private static async Task<long> ResolveUserIdForModemAsync(FocusGateDbContext db, int modemId, CancellationToken ct)
    {
        var um = await db.UserModems
            .Where(um => um.ModemId == modemId && um.RemovedAt == null)
            .FirstOrDefaultAsync(ct);
        return um?.UserId ?? 1;
    }

    private async Task<bool> HandleInsertSmsAsync(FocusGateDbContext db, SmsRecord sms, CancellationToken ct)
    {
        if (sms.SimCardId <= 0)
        {
            _logger.LogWarning("SMS has no SimCardId, discarding: Sender={Sender}", sms.SenderNumber);
            return false;
        }

        // Prevent repetition of the exact same message to keep things clear
        var lastSms = await db.SmsRecords
            .Where(s => s.SimCardId == sms.SimCardId && s.SenderNumber == sms.SenderNumber)
            .OrderByDescending(s => s.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        if (lastSms != null && lastSms.Content == sms.Content)
        {
            _logger.LogWarning("SMS duplicate skipped (exact repetition): SimCardId={SimId} Sender={Sender}", sms.SimCardId, sms.SenderNumber);
            return false;
        }

        db.SmsRecords.Add(sms);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("SMS stored: SimCardId={SimId} Sender={Sender}",
            sms.SimCardId, sms.SenderNumber);

        if (sms.SenderNumber != "MCIRMN" && sms.SenderNumber != "Mobilis" && !sms.SenderNumber.Contains("77111")) return true;

        var sim = await db.SimCards.FindAsync(new object[] { sms.SimCardId }, ct);
        if (sim == null) return true;

        var userId = await ResolveUserIdForModemAsync(db, sim.ModemId, ct);

        if (sms.Content.Contains("Solde"))
        {
            var balance = ExtractBalanceFromContent(sms.Content);
            if (balance.HasValue)
            {
                var oldBalance = sim.Balance;
                sim.Balance = balance.Value;
                sim.VerifiedAt = DateTime.UtcNow;
                sim.LastSeen = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                if (oldBalance != balance.Value)
                {
                    db.BalanceHistories.Add(new BalanceHistory
                    {
                        SimCardId = sms.SimCardId,
                        ModemId = sim.ModemId,
                        UserId = userId,
                        Balance = balance.Value,
                        PreviousBalance = oldBalance,
                        Source = BalanceSource.SMS,
                        RecordedAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("Balance from SMS: Modem={Id} {Old:F2} DZD → {New:F2} DZD", sim.ModemId, oldBalance, balance.Value);
                }
                else
                {
                    _logger.LogInformation("Balance confirmed from SMS: Modem={Id} {Balance:F2} DZD (no change)", sim.ModemId, balance.Value);
                }
            }
        }

        if (sms.Content.Contains("montant de"))
        {
            var amount = ExtractCreditAmount(sms.Content);
            db.BalanceHistories.Add(new BalanceHistory
            {
                SimCardId = sms.SimCardId,
                ModemId = sim.ModemId,
                UserId = userId,
                Balance = sim.Balance,
                PreviousBalance = null,
                Source = BalanceSource.SMS,
                RecordedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);

            if (userId > 0 && amount > 0)
            {
                CreditUserBalance(db, userId, amount, sms.SimCardId);
                await db.SaveChangesAsync(ct);
            }

            _logger.LogInformation("Credit transfer: Modem={Id} Amount={Amount} DZD", sim.ModemId, amount);
        }

        if (sms.Content.Contains("recharg"))
        {
            var amount = ExtractRechargeAmount(sms.Content);
            if (amount > 0)
            {
                var oldBalance = sim.Balance;
                sim.Balance += amount;
                sim.VerifiedAt = DateTime.UtcNow;
                sim.LastSeen = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                db.BalanceHistories.Add(new BalanceHistory
                {
                    SimCardId = sms.SimCardId,
                    ModemId = sim.ModemId,
                    UserId = userId,
                    Balance = sim.Balance,
                    PreviousBalance = oldBalance,
                    Source = BalanceSource.SMS,
                    RecordedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync(ct);

                _logger.LogInformation("Recharge from SMS: Modem={Id} +{Amount:F2} DZD → Balance {New:F2} DZD", sim.ModemId, amount, sim.Balance);
            }
        }
        return true;
    }

    private static decimal ExtractRechargeAmount(string content)
    {
        var marker = "recharg";
        var idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;

        var afterMarker = content[idx..];
        var numMatch = System.Text.RegularExpressions.Regex.Match(afterMarker, @"(\d+[\.,]?\d*)");
        if (!numMatch.Success) return 0;

        var numStr = numMatch.Groups[1].Value.Replace(",", ".");
        return decimal.TryParse(numStr, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0;
    }

    private static decimal ExtractCreditAmount(string content)
    {
        var marker = "montant de ";
        var idx = content.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return 0;
        idx += marker.Length;
        var end = content.IndexOf(" DZD", idx, StringComparison.Ordinal);
        if (end < 0) return 0;
        var numStr = content[idx..end].Replace(',', '.');
        return decimal.TryParse(numStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0;
    }

    private static decimal? ExtractBalanceFromContent(string content)
    {
        var soldeIdx = content.IndexOf("Solde", StringComparison.Ordinal);
        if (soldeIdx < 0)
            soldeIdx = content.IndexOf("solde", StringComparison.Ordinal);
        if (soldeIdx < 0) return null;

        var afterSolde = content[(soldeIdx + 5)..];
        var numMatch = System.Text.RegularExpressions.Regex.Match(afterSolde, @"(\d+[,\\.]?\d*)");
        if (!numMatch.Success) return null;

        var numStr = numMatch.Groups[1].Value.Replace(",", ".");
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
            .Where(m => m.Status != ModemStatus.Offline && m.Status != ModemStatus.Error && m.Status != ModemStatus.Detected)
            .ToListAsync(ct);

        foreach (var modem in online)
        {
            if (!activeImeis.Contains(modem.IMEI))
            {
                _logger.LogWarning("Modem {Id} ({IMEI}) orphaned -> Offline", modem.Id, modem.IMEI);
                modem.Status = ModemStatus.Offline;
                modem.ComPort = null;
                modem.UpdatedAt = DateTime.UtcNow;
            }
        }
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

    private static void CreditUserBalance(FocusGateDbContext db, long userId, decimal amount, long? simCardId)
    {
        if (amount <= 0) return;

        var user = db.Users.FirstOrDefault(u => u.Id == userId);
        if (user == null) return;

        var oldBalance = user.Balance;
        user.Balance += amount;

        db.UserBalanceHistories.Add(new UserBalanceHistory
        {
            UserId = userId,
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
