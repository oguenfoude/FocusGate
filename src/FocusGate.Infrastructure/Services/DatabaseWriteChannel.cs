using System;
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
    private readonly FocusGateMongoClient? _mongo;
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
        ProcessWithdrawal,
        TouchModemUpdatedAt,
        InsertMeetMobHistory,
        CleanupOldSms
    }

    public DatabaseWriteChannel(IServiceProvider services, ILogger<DatabaseWriteChannel> logger)
    {
        _services = services;
        _logger = logger;
        _channel = Channel.CreateUnbounded<WriteOperation>(new UnboundedChannelOptions { SingleReader = true });
        _mongo = services.GetService<FocusGateMongoClient>();
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
                    case Op.TouchModemUpdatedAt:    await HandleTouchModemUpdatedAtAsync(db, op.Data!, ct); success = true; break;
                    case Op.InsertMeetMobHistory: success = await HandleInsertMeetMobHistoryAsync(db, op.Data!, ct); break;
                    case Op.CleanupOldSms: success = await HandleCleanupOldSmsAsync(db, ct); break;
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

        if (_mongo?.IsConnected == true)
        {
            try { await _mongo.UpsertAsync(_mongo.Modems, modem, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for InsertModem — data saved to SQLite"); }
        }
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

        if (_mongo?.IsConnected == true)
        {
            try
            {
                var modem = await db.Modems.FirstOrDefaultAsync(m => m.Id == modemId, ct);
                if (modem != null) await _mongo.UpsertAsync(_mongo.Modems, modem, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for UpdateModemStatus — data saved to SQLite"); }
        }
    }

    private async Task HandleTouchModemUpdatedAtAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var now = DateTime.UtcNow;

        await db.Modems
            .Where(m => m.Id == modemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.UpdatedAt, now), ct);

        await db.SimCards
            .Where(s => s.ModemId == modemId && s.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(s => s.LastSeen, now)
                .SetProperty(s => s.UpdatedAt, now), ct);

        if (_mongo?.IsConnected == true)
        {
            try
            {
                var modem = await db.Modems.FirstOrDefaultAsync(m => m.Id == modemId, ct);
                if (modem != null) await _mongo.UpsertAsync(_mongo.Modems, modem, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for TouchModemUpdatedAt — data saved to SQLite"); }
        }
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

        if (_mongo?.IsConnected == true)
        {
            try
            {
                var modem = await db.Modems.FirstOrDefaultAsync(m => m.Id == modemId, ct);
                if (modem != null) await _mongo.UpsertAsync(_mongo.Modems, modem, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for UpdateModemComPort — data saved to SQLite"); }
        }
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

            if (_mongo?.IsConnected == true)
            {
                try { await _mongo.UpsertAsync(_mongo.SimCards, activeSim, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for UpsertSimCard — data saved to SQLite"); }
            }
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

        if (_mongo?.IsConnected == true)
        {
            try { await _mongo.UpsertAsync(_mongo.SimCards, newSim, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for UpsertSimCard — data saved to SQLite"); }
        }
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

            if (_mongo?.IsConnected == true)
            {
                try { await _mongo.UpsertAsync(_mongo.SimCards, sim, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for UpdateSimCardPhone — data saved to SQLite"); }
            }
        }
    }

    private async Task HandleDeactivateSimCardsAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var now = DateTime.UtcNow;

        var deactivated = await db.SimCards
            .Where(s => s.ModemId == modemId && s.IsActive)
            .ToListAsync(ct);

        await db.SimCards
            .Where(s => s.ModemId == modemId && s.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsActive, false)
                .SetProperty(x => x.RemovedAt, now)
                .SetProperty(x => x.LastSeen, now), ct);

        if (deactivated.Count > 0 && _mongo?.IsConnected == true)
        {
            foreach (var sim in deactivated)
            {
                sim.IsActive = false;
                sim.RemovedAt = now;
                sim.LastSeen = now;
            }
            try { await _mongo.UpsertManyAsync(_mongo.SimCards, deactivated, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for DeactivateSimCards — data saved to SQLite"); }
        }
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

            BalanceHistory? bh = null;
            if (Math.Abs(newBalance - oldBalance) > 0.01m)
            {
                var userId = await ModemHelper.ResolveUserIdForModemAsync(db, modemId, ct);

                bh = new BalanceHistory
                {
                    SimCardId = sim.Id,
                    ModemId = modemId,
                    UserId = userId,
                    Balance = newBalance,
                    PreviousBalance = oldBalance,
                    Source = source,
                    RecordedAt = DateTime.UtcNow
                };
                db.BalanceHistories.Add(bh);

                if (newBalance > oldBalance)
                    _logger.LogInformation("{Source} balance increased: Modem={ModemId} Sim={SimId} {Old:F2} → {New:F2} DZD",
                        source, modemId, sim.Id, oldBalance, newBalance);
                else
                    _logger.LogInformation("{Source} balance decreased: Modem={ModemId} Sim={SimId} {Old:F2} → {New:F2} DZD",
                        source, modemId, sim.Id, oldBalance, newBalance);
            }

            await db.SaveChangesAsync(ct);

            if (_mongo?.IsConnected == true)
            {
                try { await _mongo.UpsertAsync(_mongo.SimCards, sim, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for UpdateSimBalance (SimCard) — data saved to SQLite"); }
                if (bh != null)
                {
                    try { await _mongo.UpsertAsync(_mongo.BalanceHistories, bh, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for UpdateSimBalance (BalanceHistory) — data saved to SQLite"); }
                }
            }
            return true;
        }
        _logger.LogWarning("{Source} balance: No active SIM on modem {ModemId}", source, modemId);
        return false;
    }

    private async Task<bool> HandleInsertSmsAsync(FocusGateDbContext db, SmsRecord sms, CancellationToken ct)
    {
        if (sms.SimCardId <= 0)
        {
            _logger.LogWarning("SMS has no SimCardId, discarding: Sender={Sender}", sms.SenderNumber);
            return false;
        }

        // Simple dedup: same sender + content + SIM within 4 minutes
        var exists = await db.SmsRecords
            .AnyAsync(s => s.SimCardId == sms.SimCardId
                && s.SenderNumber == sms.SenderNumber
                && s.Content == sms.Content
                && s.ReceivedAt >= sms.ReceivedAt.AddMinutes(-4)
                && s.ReceivedAt <= sms.ReceivedAt.AddMinutes(4), ct);

        if (exists)
        {
            _logger.LogDebug("SMS duplicate skipped: SimCardId={SimId} Sender={Sender}", sms.SimCardId, sms.SenderNumber);
            return false;
        }

        db.SmsRecords.Add(sms);

        var smsType = ClassifySmsType(sms.SenderNumber, sms.Content ?? "");
        _logger.LogInformation("SMS saved: Sim={SimId} Sender={Sender} Type={Type} Content={Content}",
            sms.SimCardId, sms.SenderNumber, smsType, (sms.Content ?? "").Substring(0, Math.Min(80, sms.Content?.Length ?? 0)));

        var sender = sms.SenderNumber?.Trim() ?? "";
        var isMobilisSms = IsMobilisSender(sender);
        if (isMobilisSms)
        {
            var sim = await db.SimCards.FirstOrDefaultAsync(s => s.Id == sms.SimCardId && s.IsActive, ct);
            if (sim == null) isMobilisSms = false;
            else await ProcessMobilisSmsAsync(db, sim, sms, ct);
        }

        await db.SaveChangesAsync(ct);

        if (_mongo?.IsConnected == true)
        {
            await _mongo.WriteSmsAsync(sms, ct);
        }
        return true;
    }

    private async Task ProcessMobilisSmsAsync(FocusGateDbContext db, SimCard sim, SmsRecord sms, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        sim.VerifiedAt = now;
        sim.LastSeen = now;

        // CHECK RECHARGE FIRST — recharge SMS may contain "Solde" text (e.g. "Solde disponible: ...")
        // This ensures recharge detection is never blocked by Solde misclassification.
        if (IsRechargeSms(sms.Content))
        {
            // SMS is for detection only — MeetMob handles crediting the user wallet
            var rechargeAmount = ExtractRechargeAmountFromContent(sms.Content);
            _logger.LogInformation("RECHARGE SMS detected: Sim={SimId} Modem={ModemId} Amount={Amount} — MeetMob will handle crediting",
                sim.Id, sim.ModemId, rechargeAmount?.ToString("F2") ?? "unknown");
            return;
        }

        // THEN check Solde (balance SMS)
        if (sms.Content.Contains("Solde", StringComparison.OrdinalIgnoreCase))
        {
            var balance = ExtractBalanceFromContent(sms.Content);
            if (balance.HasValue)
            {
                var oldSimBalance = sim.Balance;
                sim.Balance = balance.Value;
                sim.VerifiedAt = now;
                sim.LastSeen = now;

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
                        RecordedAt = now
                    });

                    _logger.LogInformation("SIM Balance updated from Solde SMS: Sim={SimId} Modem={ModemId} {Old:F2} → {New:F2} DZD",
                        sim.Id, sim.ModemId, oldSimBalance, balance.Value);
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
        if (!IsMobilisSender(sender)) return "other";
        if (content.Contains("Solde", StringComparison.OrdinalIgnoreCase)) return "balance";
        if (content.Contains("montant de", StringComparison.OrdinalIgnoreCase)
            && content.Contains("reçu", StringComparison.OrdinalIgnoreCase)) return "transfer";
        if (content.Contains("rechargé", StringComparison.OrdinalIgnoreCase)) return "recharge";
        if (content.Contains("transféré", StringComparison.OrdinalIgnoreCase)) return "transfer";
        if (content.Contains("Votre offre", StringComparison.OrdinalIgnoreCase)
            || content.Contains("offre", StringComparison.OrdinalIgnoreCase)) return "offer";
        return "mobilis-other";
    }

    /// <summary>Returns true for any known Mobilis sender number or name (case-insensitive).</summary>
    internal static bool IsMobilisSender(string? sender)
    {
        if (string.IsNullOrWhiteSpace(sender)) return false;
        var s = sender.Trim();
        return s.Equals("Mobilis", StringComparison.OrdinalIgnoreCase)
            || s == "77111" || s == "610" || s == "600" || s == "666";
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
            var orphanedModems = await db.Modems
                .Where(m => m.Status != ModemStatus.Offline && m.Status != ModemStatus.Error
                         && m.Status != ModemStatus.Detected && m.Status != ModemStatus.PendingNetwork)
                .ToListAsync(ct);

            int allOrphaned = await db.Modems
                .Where(m => m.Status != ModemStatus.Offline && m.Status != ModemStatus.Error
                         && m.Status != ModemStatus.Detected && m.Status != ModemStatus.PendingNetwork)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, ModemStatus.Offline)
                    .SetProperty(m => m.ComPort, (string?)null)
                    .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);

            if (allOrphaned > 0)
            {
                _logger.LogWarning("All {Count} online modems orphaned -> Offline", allOrphaned);
                if (_mongo?.IsConnected == true)
                {
                    foreach (var m in orphanedModems) { m.Status = ModemStatus.Offline; m.ComPort = null; }
                    try { await _mongo.UpsertManyAsync(_mongo.Modems, orphanedModems, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for UpdateOrphanedModems — data saved to SQLite"); }
                }
            }
            return;
        }

        var orphaned = await db.Modems
            .Where(m => m.Status != ModemStatus.Offline && m.Status != ModemStatus.Error
                     && m.Status != ModemStatus.Detected && m.Status != ModemStatus.PendingNetwork
                     && !activeImeis.Contains(m.IMEI))
            .ToListAsync(ct);

        var orphanedCount = await db.Modems
            .Where(m => m.Status != ModemStatus.Offline && m.Status != ModemStatus.Error
                     && m.Status != ModemStatus.Detected && m.Status != ModemStatus.PendingNetwork
                     && !activeImeis.Contains(m.IMEI))
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, ModemStatus.Offline)
                .SetProperty(m => m.ComPort, (string?)null)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);

        if (orphanedCount > 0)
        {
            _logger.LogWarning("{Count} modems orphaned -> Offline (active: {ActiveCount})",
                orphanedCount, activeImeis.Count);
            if (_mongo?.IsConnected == true)
            {
                foreach (var m in orphaned) { m.Status = ModemStatus.Offline; m.ComPort = null; }
                try { await _mongo.UpsertManyAsync(_mongo.Modems, orphaned, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for UpdateOrphanedModems — data saved to SQLite"); }
            }
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

        if (_mongo?.IsConnected == true)
        {
            try { await _mongo.UpsertAsync(_mongo.WithdrawalRequests, request, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for CreateWithdrawalRequest — data saved to SQLite"); }
        }
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

        var now = DateTime.UtcNow;
        BalanceHistory? newBh = null;
        UserBalanceHistory? newUbh = null;

        if (approved)
        {
            request.Status = WithdrawalStatus.Approved;
            request.ProcessedByAdminId = adminId > 0 ? adminId : null;
            request.ProcessedAt = now;
            request.UpdatedAt = now;
            request.AdminNote = adminNote;

            var oldBalance = user.Balance;
            user.Balance = Math.Max(0, user.Balance - request.Amount);
            user.UpdatedAt = now;

            newBh = new BalanceHistory
            {
                SimCardId = null,
                ModemId = null,
                UserId = user.Id,
                Balance = user.Balance,
                PreviousBalance = oldBalance,
                Source = BalanceSource.Withdrawal,
                RecordedAt = now,
                UpdatedAt = now
            };
            db.BalanceHistories.Add(newBh);

            newUbh = new UserBalanceHistory
            {
                UserId = user.Id,
                Amount = -request.Amount,
                BalanceAfter = user.Balance,
                Type = 1,
                SimCardId = null,
                Note = $"Withdrawal approved{(string.IsNullOrEmpty(adminNote) ? "" : ": " + adminNote)}",
                RecordedAt = now,
                UpdatedAt = now
            };
            db.UserBalanceHistories.Add(newUbh);

            _logger.LogInformation("Withdrawal approved: Request={RequestId} User={UserId} Amount={Amount} DZD", requestId, user.Id, request.Amount);
        }
        else
        {
            request.Status = WithdrawalStatus.Rejected;
            request.ProcessedByAdminId = adminId > 0 ? adminId : null;
            request.ProcessedAt = now;
            request.UpdatedAt = now;
            request.AdminNote = adminNote;

            _logger.LogInformation("Withdrawal rejected: Request={RequestId} User={UserId}", requestId, user.Id);
        }

        await db.SaveChangesAsync(ct);

        if (_mongo?.IsConnected == true)
        {
            try
            {
                await _mongo.UpsertAsync(_mongo.WithdrawalRequests, request, ct);
                await _mongo.UpsertAsync(_mongo.Users, user, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for ProcessWithdrawal — data saved to SQLite"); }
            if (newBh != null)
            {
                try { await _mongo.UpsertAsync(_mongo.BalanceHistories, newBh, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for ProcessWithdrawal (BalanceHistory) — data saved to SQLite"); }
            }
            if (newUbh != null)
            {
                try { await _mongo.UpsertAsync(_mongo.UserBalanceHistories, newUbh, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for ProcessWithdrawal (UserBalanceHistory) — data saved to SQLite"); }
            }
        }
        return true;
    }

    private static (bool credited, decimal oldBalance, decimal newBalance) CreditUserBalance(FocusGateDbContext db, long? userId, decimal amount, long? simCardId, string? note = null, DateTime? recordedAt = null)
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
            Note = string.IsNullOrEmpty(note) ? "Credit from SIM" : note,
            RecordedAt = recordedAt ?? DateTime.UtcNow
        });
        return (true, oldBalance, user.Balance);
    }

    private async Task<bool> HandleInsertMeetMobHistoryAsync(FocusGateDbContext db, object data, CancellationToken ct)
    {
        var d = Deserialize(data);
        var modemId = d["ModemId"].GetInt32();
        var records = JsonSerializer.Deserialize<List<MeetMobRechargeRecordDto>>(d["Records"].GetRawText()) ?? new();

        var sim = await db.SimCards.FirstOrDefaultAsync(s => s.ModemId == modemId && s.IsActive, ct);
        if (sim == null)
        {
            _logger.LogWarning("MeetMob history: No active SIM on modem {ModemId}", modemId);
            return false;
        }

        var userId = await ModemHelper.ResolveUserIdForModemAsync(db, modemId, ct);
        var inserted = 0;
        var credited = 0;
        var totalCreditAmount = 0m;
        var creditedUserIds = new HashSet<long>();

        // Load existing MeetMob balance history for dedup
        var existingHistory = await db.BalanceHistories
            .Where(h => h.SimCardId == sim.Id && h.Source == BalanceSource.MeetMob)
            .ToListAsync(ct);
        var existingSet = new HashSet<(decimal Balance, DateTime RecordedAt)>(
            existingHistory.Select(h => (h.Balance, h.RecordedAt)));

        // Parse all records
        var parsedRecords = new List<(DateTime RecordedAt, decimal Amount, string RawTime, string RawAmount)>();
        foreach (var record in records)
        {
            if (!DateTime.TryParse(record.TradeTime, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var recordedAtLocal))
            {
                _logger.LogWarning("MeetMob history modem {ModemId}: SKIP — can't parse time '{Time}'", modemId, record.TradeTime);
                continue;
            }
            var recordedAt = recordedAtLocal.ToUniversalTime();
            if (!decimal.TryParse(MeetMobService.NormalizeMeetMobAmount(record.Amount),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            {
                _logger.LogWarning("MeetMob history modem {ModemId}: SKIP — can't parse amount '{Amount}'", modemId, record.Amount);
                continue;
            }
            parsedRecords.Add((recordedAt, amount, record.TradeTime, record.Amount));
        }

        if (parsedRecords.Count == 0)
        {
            _logger.LogInformation("MeetMob history modem {ModemId}: 0 valid records from API", modemId);
            return false;
        }

        // Sort by time ASCENDING — each record = one recharge event
        // The API field "rechargeAmount" is the individual recharge amount, NOT a balance level
        var byTime = parsedRecords
            .OrderBy(r => r.RecordedAt)
            .ToList();

        var uniqueAmounts = byTime.Count;
        _logger.LogInformation("MeetMob history modem {ModemId}: {RawCount} raw → {UniqueCount} records by time, sim={SimBalance:F2}, user={UserId})",
            modemId, parsedRecords.Count, uniqueAmounts, sim.Balance, userId);

        foreach (var (recordedAt, amount, rawTime, rawAmount) in byTime)
        {
            // Dedup: already credited in BalanceHistory?
            if (existingSet.Contains((amount, recordedAt)))
            {
                _logger.LogDebug("MeetMob history modem {ModemId}: SKIP [{Time}] {Amount} — already credited", modemId, rawTime, rawAmount);
                continue;
            }

            // Skip zero/negative amounts
            if (amount <= 0)
            {
                _logger.LogDebug("MeetMob history modem {ModemId}: SKIP [{Time}] {Amount} — zero/negative", modemId, rawTime, rawAmount);
                continue;
            }

            // New recharge — credit amount directly (amount IS the recharge amount)
            sim.VerifiedAt = DateTime.UtcNow;
            sim.LastSeen = DateTime.UtcNow;

            var bh = new BalanceHistory
            {
                SimCardId = sim.Id,
                ModemId = modemId,
                UserId = userId > 0 ? userId : null,
                Balance = sim.Balance,
                PreviousBalance = sim.Balance - amount,
                Source = BalanceSource.MeetMob,
                RecordedAt = recordedAt
            };
            db.BalanceHistories.Add(bh);
            inserted++;

            _logger.LogInformation("MeetMob history modem {ModemId}: RECHARGE [{Time}] +{Amount:F2} DZD (sim balance={SimBalance:F2})",
                modemId, rawTime, amount, sim.Balance);

            if (userId > 0)
            {
                var creditResult = CreditUserBalance(db, userId, amount, sim.Id, $"MeetMob recharge ({rawTime})", recordedAt);
                if (creditResult.credited)
                {
                    credited++;
                    totalCreditAmount += amount;
                    creditedUserIds.Add(userId!.Value);
                    _logger.LogInformation("MeetMob history modem {ModemId}: CREDIT +{Amount:F2} DZD → user {UserId}",
                        modemId, amount, userId);
                }
                else
                {
                    _logger.LogWarning("MeetMob history modem {ModemId}: CREDIT FAILED +{Amount:F2} DZD → user {UserId}",
                        modemId, amount, userId);
                }
            }
        }

        if (inserted > 0)
        {
            var newBalanceHistories = db.ChangeTracker.Entries<BalanceHistory>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity).ToList();
            var newUbh = db.ChangeTracker.Entries<UserBalanceHistory>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity).ToList();

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("MeetMob history modem {ModemId}: DONE — inserted={Inserted}, credited={Credited}, totalCredit={Total:F2} DZD, user={UserId}",
                modemId, inserted, credited, totalCreditAmount, userId);

            if (_mongo?.IsConnected == true)
            {
                try { await _mongo.UpsertAsync(_mongo.SimCards, sim, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for InsertMeetMobHistory (SimCard) — data saved to SQLite"); }
                if (newBalanceHistories.Count > 0)
                {
                    try { await _mongo.InsertManyAsync(_mongo.BalanceHistories, newBalanceHistories, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for InsertMeetMobHistory (BalanceHistory) — data saved to SQLite"); }
                }
                if (creditedUserIds.Count > 0)
                {
                    foreach (var uid in creditedUserIds)
                    {
                        try
                        {
                            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);
                            if (user != null) await _mongo.UpsertAsync(_mongo.Users, user, ct);
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for InsertMeetMobHistory (User) — data saved to SQLite"); }
                    }
                }
                if (newUbh.Count > 0)
                {
                    try { await _mongo.InsertManyAsync(_mongo.UserBalanceHistories, newUbh, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "MongoDB write failed for InsertMeetMobHistory (UserBalanceHistory) — data saved to SQLite"); }
                }
            }
        }

        return inserted > 0;
    }

    private static async Task<bool> HandleCleanupOldSmsAsync(FocusGateDbContext db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-60);
        var deleted = await db.SmsRecords.Where(s => s.ReceivedAt < cutoff).ExecuteDeleteAsync(ct);
        if (deleted > 0)
        {
            // Also remove from MongoDB if connected
            // Note: SMS records are not synced to MongoDB, so no MongoDB cleanup needed
        }
        return deleted > 0;
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
