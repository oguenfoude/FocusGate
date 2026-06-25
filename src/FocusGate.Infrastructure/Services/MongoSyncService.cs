using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace FocusGate.Infrastructure.Services;

public class MongoSyncService : BackgroundService
{
    private readonly FocusGateMongoClient _mongo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MongoSyncService> _logger;
    private readonly string _machineId;
    private readonly int _intervalSeconds;

    private DateTime _lastSyncAt = DateTime.MinValue;
    private bool _initialSyncDone = false;
    private DateTime _lastSyncStarted = DateTime.MinValue;
    private DateTime _lastSyncCompleted = DateTime.MinValue;
    private int _totalPushed = 0;
    private int _totalPulled = 0;
    private string _lastError = "";

    public DateTime LastSyncStarted => _lastSyncStarted;
    public DateTime LastSyncCompleted => _lastSyncCompleted;
    public int TotalPushed => _totalPushed;
    public int TotalPulled => _totalPulled;
    public string LastError => _lastError;
    public bool IsConnected => _mongo.IsConnected;
    public string MachineId => _machineId;

    public MongoSyncService(
        FocusGateMongoClient mongo,
        IServiceScopeFactory scopeFactory,
        ILogger<MongoSyncService> logger,
        string machineId,
        int intervalSeconds = 30)
    {
        _mongo = mongo;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _machineId = machineId;
        _intervalSeconds = intervalSeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MongoSync started (interval: {Interval}s, machine: {Machine})",
            _intervalSeconds, _machineId);

        var connected = await _mongo.TestConnectionAsync();
        if (!connected)
        {
            _logger.LogWarning("MongoDB unavailable — sync disabled (app works without cloud)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_mongo.IsConnected)
                {
                    await _mongo.TestConnectionAsync();
                    if (!_mongo.IsConnected)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                        continue;
                    }
                    _logger.LogInformation("MongoDB now available — resuming sync");
                }

                _lastSyncStarted = DateTime.UtcNow;
                _lastError = "";
                if (!_initialSyncDone)
                {
                    await FullSyncAsync(stoppingToken);
                    _initialSyncDone = true;
                }
                else
                {
                    await IncrementalSyncAsync(stoppingToken);
                }
                _lastSyncCompleted = DateTime.UtcNow;
            }
            catch (OperationCanceledException) { break; }
            catch (InvalidOperationException ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning("MongoDB unavailable — retrying in 60s: {Error}", ex.Message);
                _mongo.IsConnected = false;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogError(ex, "Sync cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken);
        }
    }

    private async Task FullSyncAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting full sync for machine {Machine}...", _machineId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        await PushToMongoAsync(db, ct);
        await PullFromMongoAsync(db, ct);

        _lastSyncAt = DateTime.UtcNow;
        _logger.LogInformation("Full sync complete");
    }

    private async Task IncrementalSyncAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

        await PushToMongoAsync(db, ct);
        await PullFromMongoAsync(db, ct);

        _lastSyncAt = DateTime.UtcNow;
    }

    private async Task PushToMongoAsync(FocusGateDbContext db, CancellationToken ct)
    {
        var since = _lastSyncAt;
        var pushed = 0;
        var skipped = 0;

        var modems = await db.Modems.IgnoreQueryFilters().Where(m => m.UpdatedAt > since).ToListAsync(ct);
        foreach (var m in modems)
        {
            m.MachineId = _machineId;
            if (!await SafeUpsertAsync(_mongo.Modems, m, ct)) skipped++;
        }
        pushed += modems.Count;

        var sims = await db.SimCards.IgnoreQueryFilters().Where(s => s.UpdatedAt > since).ToListAsync(ct);
        foreach (var s in sims)
        {
            s.MachineId = _machineId;
            if (!await SafeUpsertAsync(_mongo.SimCards, s, ct)) skipped++;
        }
        pushed += sims.Count;

        var sms = await db.SmsRecords.IgnoreQueryFilters().Where(s => s.UpdatedAt > since).ToListAsync(ct);
        foreach (var s in sms)
        {
            s.MachineId = _machineId;
            if (!await SafeUpsertAsync(_mongo.SmsRecords, s, ct)) skipped++;
        }
        pushed += sms.Count;

        var balances = await db.BalanceHistories.IgnoreQueryFilters().Where(b => b.UpdatedAt > since).ToListAsync(ct);
        foreach (var b in balances)
        {
            b.MachineId = _machineId;
            if (!await SafeUpsertAsync(_mongo.BalanceHistories, b, ct)) skipped++;
        }
        pushed += balances.Count;

        var users = await db.Users.IgnoreQueryFilters().Where(u => u.UpdatedAt > since).ToListAsync(ct);
        foreach (var u in users)
        {
            u.MachineId = _machineId;
            if (!await SafeUpsertAsync(_mongo.Users, u, ct)) skipped++;
        }
        pushed += users.Count;

        var userModems = await db.UserModems.IgnoreQueryFilters().Where(um => um.UpdatedAt > since).ToListAsync(ct);
        foreach (var um in userModems)
        {
            um.MachineId = _machineId;
            if (!await SafeUpsertAsync(_mongo.UserModems, um, ct)) skipped++;
        }
        pushed += userModems.Count;

        var withdrawalRequests = await db.WithdrawalRequests.IgnoreQueryFilters().Where(w => w.UpdatedAt > since).ToListAsync(ct);
        foreach (var w in withdrawalRequests)
        {
            w.MachineId = _machineId;
            if (!await SafeUpsertAsync(_mongo.WithdrawalRequests, w, ct)) skipped++;
        }
        pushed += withdrawalRequests.Count;

        var userBalanceHistories = await db.UserBalanceHistories.IgnoreQueryFilters().Where(ub => ub.UpdatedAt > since).ToListAsync(ct);
        foreach (var ub in userBalanceHistories)
        {
            ub.MachineId = _machineId;
            if (!await SafeUpsertAsync(_mongo.UserBalanceHistories, ub, ct)) skipped++;
        }
        pushed += userBalanceHistories.Count;

        if (pushed > 0)
            _logger.LogInformation("Pushed {Count} records to MongoDB (skipped {Skipped}, machine: {Machine})", pushed, skipped, _machineId);
        _totalPushed += pushed;
    }

    private async Task<bool> SafeUpsertAsync<T>(IMongoCollection<T> collection, T document, CancellationToken ct) where T : class
    {
        try
        {
            var idProp = typeof(T).GetProperty("Id")!;
            var machineIdProp = typeof(T).GetProperty("MachineId")!;
            var id = idProp.GetValue(document)!;
            var machineId = machineIdProp.GetValue(document)!;

            var filter = Builders<T>.Filter.And(
                Builders<T>.Filter.Eq("Id", id),
                Builders<T>.Filter.Eq("MachineId", machineId));

            await collection.ReplaceOneAsync(filter, document,
                new ReplaceOptions { IsUpsert = true }, ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            try
            {
                var idProp = typeof(T).GetProperty("Id")!;
                var id = idProp.GetValue(document)!;

                var idFilter = Builders<T>.Filter.Eq("Id", id);
                await collection.ReplaceOneAsync(idFilter, document, new ReplaceOptions { IsUpsert = false }, ct);
                _logger.LogInformation("Claimed {Type} Id={Id} (overwrote old machine)", typeof(T).Name, id);
                return true;
            }
            catch (Exception ex2)
            {
                _logger.LogWarning(ex2, "Failed to claim {Type} Id={Id}", typeof(T).Name,
                    typeof(T).GetProperty("Id")?.GetValue(document));
                return false;
            }
        }
    }

    private async Task PullFromMongoAsync(FocusGateDbContext db, CancellationToken ct)
    {
        var since = _lastSyncAt;
        var pulled = 0;

        var machineFilter = Builders<Modem>.Filter.And(
            Builders<Modem>.Filter.Eq(x => x.MachineId, _machineId),
            since == DateTime.MinValue
                ? FilterDefinition<Modem>.Empty
                : Builders<Modem>.Filter.Gt(x => x.UpdatedAt, since));
        var mongoModems = await _mongo.Modems.Find(machineFilter).ToListAsync(ct);
        foreach (var m in mongoModems)
        {
            var local = await db.Modems.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == m.Id, ct);
            if (local == null)
            {
                m.MachineId = _machineId;
                db.Modems.Add(m);
            }
            else if (m.UpdatedAt > local.UpdatedAt)
            {
                local.IMEI = m.IMEI;
                local.ComPort = m.ComPort;
                local.Status = m.Status;
                local.UpdatedAt = m.UpdatedAt;
                local.ArchivedAt = m.ArchivedAt;
                local.MachineId = _machineId;
            }
        }
        pulled += mongoModems.Count;
        await SafeSaveAsync(db, "modems", ct);

        var simMachineFilter = Builders<SimCard>.Filter.And(
            Builders<SimCard>.Filter.Eq(x => x.MachineId, _machineId),
            since == DateTime.MinValue
                ? FilterDefinition<SimCard>.Empty
                : Builders<SimCard>.Filter.Gt(x => x.UpdatedAt, since));
        var mongoSims = await _mongo.SimCards.Find(simMachineFilter).ToListAsync(ct);
        foreach (var s in mongoSims)
        {
            var local = await db.SimCards.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == s.Id, ct);
            if (local == null)
            {
                s.MachineId = _machineId;
                db.SimCards.Add(s);
            }
            else if (s.UpdatedAt > local.UpdatedAt)
            {
                local.IMSI = s.IMSI;
                local.PhoneNumber = s.PhoneNumber;
                local.Balance = s.Balance;
                local.VerifiedAt = s.VerifiedAt;
                local.IsActive = s.IsActive;
                local.Status = s.Status;
                local.FirstSeen = s.FirstSeen;
                local.LastSeen = s.LastSeen;
                local.RemovedAt = s.RemovedAt;
                local.ReplacedAt = s.ReplacedAt;
                local.UpdatedAt = s.UpdatedAt;
                local.ArchivedAt = s.ArchivedAt;
                local.MachineId = _machineId;
            }
        }
        await SafeSaveAsync(db, "simcards", ct);

        var smsMachineFilter = Builders<SmsRecord>.Filter.And(
            Builders<SmsRecord>.Filter.Eq(x => x.MachineId, _machineId),
            since == DateTime.MinValue
                ? FilterDefinition<SmsRecord>.Empty
                : Builders<SmsRecord>.Filter.Gt(x => x.UpdatedAt, since));
        var mongoSms = await _mongo.SmsRecords.Find(smsMachineFilter).ToListAsync(ct);
        foreach (var s in mongoSms)
        {
            var local = await db.SmsRecords.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == s.Id, ct);
            if (local == null)
            {
                s.MachineId = _machineId;
                db.SmsRecords.Add(s);
            }
            else if (s.UpdatedAt > local.UpdatedAt)
            {
                local.SenderNumber = s.SenderNumber;
                local.Content = s.Content;
                local.ReceivedAt = s.ReceivedAt;
                local.ProcessedAt = s.ProcessedAt;
                local.UpdatedAt = s.UpdatedAt;
                local.ArchivedAt = s.ArchivedAt;
                local.MachineId = _machineId;
            }
        }
        await SafeSaveAsync(db, "smsrecords", ct);

        var balMachineFilter = Builders<BalanceHistory>.Filter.And(
            Builders<BalanceHistory>.Filter.Eq(x => x.MachineId, _machineId),
            since == DateTime.MinValue
                ? FilterDefinition<BalanceHistory>.Empty
                : Builders<BalanceHistory>.Filter.Gt(x => x.UpdatedAt, since));
        var mongoBalances = await _mongo.BalanceHistories.Find(balMachineFilter).ToListAsync(ct);
        foreach (var b in mongoBalances)
        {
            var local = await db.BalanceHistories.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == b.Id, ct);
            if (local == null)
            {
                b.MachineId = _machineId;
                db.BalanceHistories.Add(b);
            }
            else if (b.UpdatedAt > local.UpdatedAt)
            {
                local.Balance = b.Balance;
                local.PreviousBalance = b.PreviousBalance;
                local.Source = b.Source;
                local.RecordedAt = b.RecordedAt;
                local.UpdatedAt = b.UpdatedAt;
                local.ArchivedAt = b.ArchivedAt;
                local.MachineId = _machineId;
            }
        }
        await SafeSaveAsync(db, "balancehistories", ct);

        var userMachineFilter = Builders<User>.Filter.And(
            Builders<User>.Filter.Eq(x => x.MachineId, _machineId),
            since == DateTime.MinValue
                ? FilterDefinition<User>.Empty
                : Builders<User>.Filter.Gt(x => x.UpdatedAt, since));
        var mongoUsers = await _mongo.Users.Find(userMachineFilter).ToListAsync(ct);
        foreach (var u in mongoUsers)
        {
            var local = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == u.Id, ct);
            if (local == null)
            {
                u.MachineId = _machineId;
                db.Users.Add(u);
            }
            else if (u.UpdatedAt > local.UpdatedAt)
            {
                local.Username = u.Username;
                local.Password = u.Password;
                local.DisplayName = u.DisplayName;
                local.Role = u.Role;
                local.IsActive = u.IsActive;
                local.Balance = u.Balance;
                local.UpdatedAt = u.UpdatedAt;
                local.ArchivedAt = u.ArchivedAt;
                local.MachineId = _machineId;
            }
        }
        await SafeSaveAsync(db, "users", ct);

        var umMachineFilter = Builders<UserModem>.Filter.And(
            Builders<UserModem>.Filter.Eq(x => x.MachineId, _machineId),
            since == DateTime.MinValue
                ? FilterDefinition<UserModem>.Empty
                : Builders<UserModem>.Filter.Gt(x => x.UpdatedAt, since));
        var mongoUserModems = await _mongo.UserModems.Find(umMachineFilter).ToListAsync(ct);
        foreach (var um in mongoUserModems)
        {
            var local = await db.UserModems.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == um.Id, ct);
            if (local == null)
            {
                um.MachineId = _machineId;
                db.UserModems.Add(um);
            }
            else if (um.UpdatedAt > local.UpdatedAt)
            {
                local.UserId = um.UserId;
                local.ModemId = um.ModemId;
                local.AssignedAt = um.AssignedAt;
                local.RemovedAt = um.RemovedAt;
                local.UpdatedAt = um.UpdatedAt;
                local.ArchivedAt = um.ArchivedAt;
                local.MachineId = _machineId;
            }
        }
        await SafeSaveAsync(db, "usermodems", ct);

        var wrMachineFilter = Builders<WithdrawalRequest>.Filter.And(
            Builders<WithdrawalRequest>.Filter.Eq(x => x.MachineId, _machineId),
            since == DateTime.MinValue
                ? FilterDefinition<WithdrawalRequest>.Empty
                : Builders<WithdrawalRequest>.Filter.Gt(x => x.UpdatedAt, since));
        var mongoWithdrawals = await _mongo.WithdrawalRequests.Find(wrMachineFilter).ToListAsync(ct);
        foreach (var w in mongoWithdrawals)
        {
            var local = await db.WithdrawalRequests.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == w.Id, ct);
            if (local == null)
            {
                w.MachineId = _machineId;
                db.WithdrawalRequests.Add(w);
            }
            else if (w.UpdatedAt > local.UpdatedAt)
            {
                local.UserId = w.UserId;
                local.Amount = w.Amount;
                local.Status = w.Status;
                local.Note = w.Note;
                local.AdminNote = w.AdminNote;
                local.ProcessedByAdminId = w.ProcessedByAdminId;
                local.RequestedAt = w.RequestedAt;
                local.ProcessedAt = w.ProcessedAt;
                local.UpdatedAt = w.UpdatedAt;
                local.ArchivedAt = w.ArchivedAt;
                local.MachineId = _machineId;
            }
        }
        await SafeSaveAsync(db, "withdrawalrequests", ct);

        var ubhMachineFilter = Builders<UserBalanceHistory>.Filter.And(
            Builders<UserBalanceHistory>.Filter.Eq(x => x.MachineId, _machineId),
            since == DateTime.MinValue
                ? FilterDefinition<UserBalanceHistory>.Empty
                : Builders<UserBalanceHistory>.Filter.Gt(x => x.UpdatedAt, since));
        var mongoUserBalances = await _mongo.UserBalanceHistories.Find(ubhMachineFilter).ToListAsync(ct);
        foreach (var ub in mongoUserBalances)
        {
            var local = await db.UserBalanceHistories.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == ub.Id, ct);
            if (local == null)
            {
                ub.MachineId = _machineId;
                db.UserBalanceHistories.Add(ub);
            }
            else if (ub.UpdatedAt > local.UpdatedAt)
            {
                local.UserId = ub.UserId;
                local.Amount = ub.Amount;
                local.BalanceAfter = ub.BalanceAfter;
                local.Type = ub.Type;
                local.SimCardId = ub.SimCardId;
                local.Note = ub.Note;
                local.RecordedAt = ub.RecordedAt;
                local.UpdatedAt = ub.UpdatedAt;
                local.ArchivedAt = ub.ArchivedAt;
                local.MachineId = _machineId;
            }
        }
        await SafeSaveAsync(db, "userbalancehistories", ct);

        if (pulled > 0)
            _logger.LogInformation("Pulled {Count} records from MongoDB (machine: {Machine})", pulled, _machineId);
        _totalPulled += pulled;
    }

    private async Task SafeSaveAsync(FocusGateDbContext db, string collection, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Sync pull skipped duplicate keys in {Collection}", collection);
            db.ChangeTracker.Clear();
        }
    }
}
