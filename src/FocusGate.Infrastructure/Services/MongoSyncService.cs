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

    private const int MaxRetryAttempts = 5;
    private const int RetryDelaySeconds = 30;
    private const int StartupDelaySeconds = 15;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MongoSync started (interval: {Interval}s, machine: {Machine})",
            _intervalSeconds, _machineId);

        _logger.LogInformation("Waiting {Delay}s for modems to initialize before connecting to MongoDB...",
            StartupDelaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds), stoppingToken);

        var retryCount = 0;
        while (retryCount < MaxRetryAttempts)
        {
            try
            {
                var connected = await _mongo.TestConnectionAsync();
                if (connected)
                {
                    _logger.LogInformation("MongoDB connected successfully (attempt {Attempt})", retryCount + 1);
                    break;
                }

                retryCount++;
                if (retryCount >= MaxRetryAttempts)
                {
                    _logger.LogWarning("MongoDB unavailable after {Attempts} attempts — sync disabled (app works without cloud)", retryCount);
                    return;
                }

                _logger.LogWarning("MongoDB connection failed (attempt {Attempt}/{Max}), retrying in {Delay}s...",
                    retryCount, MaxRetryAttempts, RetryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                retryCount++;
                _logger.LogWarning(ex, "MongoDB connection error (attempt {Attempt}/{Max}), retrying in {Delay}s...",
                    retryCount, MaxRetryAttempts, RetryDelaySeconds);
                if (retryCount >= MaxRetryAttempts)
                {
                    _logger.LogWarning("MongoDB unavailable after {Attempts} attempts — sync disabled (app works without cloud)", retryCount);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), stoppingToken);
            }
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
                        await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), stoppingToken);
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
                _logger.LogWarning("MongoDB sync error — retrying in {Delay}s: {Error}", RetryDelaySeconds, ex.Message);
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MongoSync stopping — performing final sync...");
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
            await PushToMongoAsync(db, cancellationToken);
            _logger.LogInformation("Final sync complete");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Final sync failed: {Error}", ex.Message);
        }
        await base.StopAsync(cancellationToken);
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

        var modemFilter = since == DateTime.MinValue
            ? FilterDefinition<Modem>.Empty
            : Builders<Modem>.Filter.Gt(x => x.UpdatedAt, since);
        pulled += await PullCollectionAsync(db, _mongo.Modems, modemFilter,
            m => m.Id, (local, m) =>
            {
                local.IMEI = m.IMEI;
                local.ComPort = m.ComPort;
                local.Status = m.Status;
                local.UpdatedAt = m.UpdatedAt;
                local.ArchivedAt = m.ArchivedAt;
                local.MachineId = _machineId;
            }, "modems", ct);

        var simFilter = since == DateTime.MinValue
            ? FilterDefinition<SimCard>.Empty
            : Builders<SimCard>.Filter.Gt(x => x.UpdatedAt, since);
        pulled += await PullCollectionAsync(db, _mongo.SimCards, simFilter,
            s => s.Id, (local, s) =>
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
            }, "simcards", ct);

        var smsFilter = since == DateTime.MinValue
            ? FilterDefinition<SmsRecord>.Empty
            : Builders<SmsRecord>.Filter.Gt(x => x.UpdatedAt, since);
        pulled += await PullCollectionAsync(db, _mongo.SmsRecords, smsFilter,
            s => s.Id, (local, s) =>
            {
                local.SenderNumber = s.SenderNumber;
                local.Content = s.Content;
                local.ReceivedAt = s.ReceivedAt;
                local.ProcessedAt = s.ProcessedAt;
                local.UpdatedAt = s.UpdatedAt;
                local.ArchivedAt = s.ArchivedAt;
                local.MachineId = _machineId;
            }, "smsrecords", ct);

        var balFilter = since == DateTime.MinValue
            ? FilterDefinition<BalanceHistory>.Empty
            : Builders<BalanceHistory>.Filter.Gt(x => x.UpdatedAt, since);
        pulled += await PullCollectionAsync(db, _mongo.BalanceHistories, balFilter,
            b => b.Id, (local, b) =>
            {
                local.Balance = b.Balance;
                local.PreviousBalance = b.PreviousBalance;
                local.Source = b.Source;
                local.RecordedAt = b.RecordedAt;
                local.UpdatedAt = b.UpdatedAt;
                local.ArchivedAt = b.ArchivedAt;
                local.MachineId = _machineId;
            }, "balancehistories", ct);

        var userFilter = since == DateTime.MinValue
            ? FilterDefinition<User>.Empty
            : Builders<User>.Filter.Gt(x => x.UpdatedAt, since);
        pulled += await PullCollectionAsync(db, _mongo.Users, userFilter,
            u => u.Id, (local, u) =>
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
            }, "users", ct);

        var umFilter = since == DateTime.MinValue
            ? FilterDefinition<UserModem>.Empty
            : Builders<UserModem>.Filter.Gt(x => x.UpdatedAt, since);
        pulled += await PullCollectionAsync(db, _mongo.UserModems, umFilter,
            um => um.Id, (local, um) =>
            {
                local.UserId = um.UserId;
                local.ModemId = um.ModemId;
                local.AssignedAt = um.AssignedAt;
                local.RemovedAt = um.RemovedAt;
                local.UpdatedAt = um.UpdatedAt;
                local.ArchivedAt = um.ArchivedAt;
                local.MachineId = _machineId;
            }, "usermodems", ct);

        var wrFilter = since == DateTime.MinValue
            ? FilterDefinition<WithdrawalRequest>.Empty
            : Builders<WithdrawalRequest>.Filter.Gt(x => x.UpdatedAt, since);
        pulled += await PullCollectionAsync(db, _mongo.WithdrawalRequests, wrFilter,
            w => w.Id, (local, w) =>
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
            }, "withdrawalrequests", ct);

        var ubhFilter = since == DateTime.MinValue
            ? FilterDefinition<UserBalanceHistory>.Empty
            : Builders<UserBalanceHistory>.Filter.Gt(x => x.UpdatedAt, since);
        pulled += await PullCollectionAsync(db, _mongo.UserBalanceHistories, ubhFilter,
            ub => ub.Id, (local, ub) =>
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
            }, "userbalancehistories", ct);

        if (pulled > 0)
            _logger.LogInformation("Pulled {Count} records from MongoDB (machine: {Machine})", pulled, _machineId);
        _totalPulled += pulled;
    }

    private async Task<int> PullCollectionAsync<T>(
        FocusGateDbContext db,
        IMongoCollection<T> collection,
        FilterDefinition<T> filter,
        Func<T, long> getId,
        Action<T, T> updateFields,
        string collectionName,
        CancellationToken ct) where T : class, new()
    {
        try
        {
            var mongoDocs = await collection.Find(filter).ToListAsync(ct);
            if (mongoDocs.Count == 0) return 0;

            var ids = mongoDocs.Select(getId).Distinct().ToList();
            var localDocs = await db.Set<T>().IgnoreQueryFilters()
                .Where(x => ids.Contains(getId(x)))
                .ToListAsync(ct);
            var localMap = localDocs.ToDictionary(getId);

            var count = 0;
            foreach (var m in mongoDocs)
            {
                var id = getId(m);
                if (localMap.TryGetValue(id, out var local))
                {
                    updateFields(local, m);
                }
                else
                {
                    db.Set<T>().Add(m);
                    localMap[id] = m;
                }
                count++;
            }
            await SafeSaveAsync(db, collectionName, ct);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pull from {Collection} skipped — will retry next cycle", collectionName);
            db.ChangeTracker.Clear();
            return 0;
        }
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
