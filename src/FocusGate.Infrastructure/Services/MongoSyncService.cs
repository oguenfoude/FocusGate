using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Linq.Expressions;

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
    private int _totalPulled = 0;
    private string _lastError = "";
    private DateTime _reconcileCutoffUtc = DateTime.UtcNow;

    public DateTime LastSyncStarted => _lastSyncStarted;
    public DateTime LastSyncCompleted => _lastSyncCompleted;
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

    private const int RetryDelaySeconds = 30;
    private const int MaxRetryDelaySeconds = 300;
    private const int StartupDelaySeconds = 5;

    private int GetRetryDelay(int retryCount)
    {
        var delay = RetryDelaySeconds * (int)Math.Pow(2, Math.Min(retryCount, 5));
        return Math.Min(delay, MaxRetryDelaySeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MongoSync started — pull-only mode (interval: {Interval}s, machine: {Machine})",
            _intervalSeconds, _machineId);

        _logger.LogInformation("Waiting {Delay}s for modems to initialize before connecting to MongoDB...",
            StartupDelaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds), stoppingToken);

        var retryCount = 0;
        while (!stoppingToken.IsCancellationRequested)
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
                var delay = GetRetryDelay(retryCount);
                if (retryCount % 5 == 0)
                    _logger.LogWarning("MongoDB still disconnected after {Count} attempts — check network/firewall/atlas IP whitelist (next retry in {Delay}s)", retryCount, delay);
                else
                    _logger.LogWarning("MongoDB connection failed (attempt {Attempt}), retrying in {Delay}s...",
                        retryCount, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                retryCount++;
                var delay = GetRetryDelay(retryCount);
                _logger.LogWarning(ex, "MongoDB connection error (attempt {Attempt}), retrying in {Delay}s...",
                    retryCount, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            Heartbeat.Pulse("mongo-sync");
            try
            {
                if (!_mongo.IsConnected)
                {
                    await _mongo.TestConnectionAsync();
                    if (!_mongo.IsConnected)
                    {
                        var reconnectDelay = GetRetryDelay(retryCount);
                        retryCount++;
                        await Task.Delay(TimeSpan.FromSeconds(reconnectDelay), stoppingToken);
                        continue;
                    }
                    _logger.LogInformation("MongoDB now available — resuming sync");
                    retryCount = 0;
                }

                _lastSyncStarted = DateTime.UtcNow;
                _lastError = "";
                if (!_initialSyncDone)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
                    await FullSyncAsync(db, stoppingToken);
                    _initialSyncDone = true;
                }
                else
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
                    await IncrementalSyncAsync(db, stoppingToken);
                }
                _lastSyncCompleted = DateTime.UtcNow;

                // Safety net: re-push any entity whose inline MongoDB write was missed
                // (e.g. brief internet blip at write time). Guarantees eventual consistency.
                await ReconcilePushAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (InvalidOperationException ex)
            {
                _lastError = ex.Message;
                var errDelay = GetRetryDelay(retryCount);
                retryCount++;
                _logger.LogWarning("MongoDB sync error — retrying in {Delay}s: {Error}", errDelay, ex.Message);
                _mongo.IsConnected = false;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                retryCount++;
                _logger.LogError(ex, "Sync cycle failed (next retry in {Delay}s)", GetRetryDelay(retryCount));
            }

            await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MongoSync stopping — final pull...");
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
            await PullFromMongoAsync(db, _lastSyncAt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Final pull failed");
        }
        await base.StopAsync(cancellationToken);
    }

    private async Task ReconcilePushAsync(CancellationToken ct)
    {
        try
        {
            var since = _reconcileCutoffUtc.AddMinutes(-2); // 2min overlap guards clock skew
            var cutoff = DateTime.UtcNow;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();

            var modems = await db.Modems.AsNoTracking().Where(m => m.UpdatedAt >= since).ToListAsync(ct);
            var sims = await db.SimCards.AsNoTracking().Where(s => s.UpdatedAt >= since).ToListAsync(ct);
            var bhs = await db.BalanceHistories.AsNoTracking().Where(b => b.UpdatedAt >= since).ToListAsync(ct);
            var ubhs = await db.UserBalanceHistories.AsNoTracking().Where(u => u.UpdatedAt >= since).ToListAsync(ct);
            var sms = await db.SmsRecords.AsNoTracking().Where(s => s.UpdatedAt >= since).OrderByDescending(s => s.ReceivedAt).Take(500).ToListAsync(ct);

            int pushed = 0;
            if (modems.Count > 0) pushed += await _mongo.UpsertManyAsync(_mongo.Modems, modems, ct);
            if (sims.Count > 0) pushed += await _mongo.UpsertManyAsync(_mongo.SimCards, sims, ct);
            if (bhs.Count > 0) pushed += await _mongo.UpsertManyAsync(_mongo.BalanceHistories, bhs, ct);
            if (ubhs.Count > 0) pushed += await _mongo.UpsertManyAsync(_mongo.UserBalanceHistories, ubhs, ct);
            if (sms.Count > 0) pushed += await _mongo.UpsertManyAsync(_mongo.SmsRecords, sms, ct);

            _reconcileCutoffUtc = cutoff;
            if (pushed > 0)
                _logger.LogInformation("Reconcile push: {Count} entities re-synced to MongoDB ({Modems} modems, {Sims} sims, {Sms} sms)", pushed, modems.Count, sims.Count, sms.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconcile push failed — will retry next cycle");
        }
    }

    private async Task FullSyncAsync(FocusGateDbContext db, CancellationToken ct)
    {
        _logger.LogInformation("Full sync started (pull-only for user data)");
        await PullFromMongoAsync(db, DateTime.MinValue, ct);
        _lastSyncAt = DateTime.UtcNow;
        _logger.LogInformation("Full sync completed");
    }

    private async Task IncrementalSyncAsync(FocusGateDbContext db, CancellationToken ct)
    {
        var since = _lastSyncAt;
        _logger.LogDebug("Incremental sync since {Since:O}", since);
        await PullFromMongoAsync(db, since, ct);
        _lastSyncAt = DateTime.UtcNow;
    }

    private async Task PullFromMongoAsync(FocusGateDbContext db, DateTime since, CancellationToken ct)
    {
        // Only pull user-originated data (written by Dashboard/Next.js)
        // Skip modem-originated data (written directly by .NET gateway)
        var pulled = 0;

        var userFilter = since == DateTime.MinValue
            ? FilterDefinition<User>.Empty
            : Builders<User>.Filter.Gt(x => x.UpdatedAt, since);
        var userPulled = await PullCollectionAsync(db, _mongo.Users, userFilter,
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
            }, "users", ct);
        pulled += userPulled;

        var umFilter = since == DateTime.MinValue
            ? FilterDefinition<UserModem>.Empty
            : Builders<UserModem>.Filter.Gt(x => x.UpdatedAt, since);
        var umPulled = await PullCollectionAsync(db, _mongo.UserModems, umFilter,
            um => um.Id, (local, um) =>
            {
                local.UserId = um.UserId;
                local.ModemId = um.ModemId;
                local.AssignedAt = um.AssignedAt;
                local.RemovedAt = um.RemovedAt;
                local.UpdatedAt = um.UpdatedAt;
                local.ArchivedAt = um.ArchivedAt;
            }, "usermodems", ct);
        pulled += umPulled;

        var wrFilter = since == DateTime.MinValue
            ? FilterDefinition<WithdrawalRequest>.Empty
            : Builders<WithdrawalRequest>.Filter.Gt(x => x.UpdatedAt, since);
        var wrPulled = await PullCollectionAsync(db, _mongo.WithdrawalRequests, wrFilter,
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
            }, "withdrawalrequests", ct);
        pulled += wrPulled;

        var ubhFilter = since == DateTime.MinValue
            ? FilterDefinition<UserBalanceHistory>.Empty
            : Builders<UserBalanceHistory>.Filter.Gt(x => x.UpdatedAt, since);
        var ubhPulled = await PullCollectionAsync(db, _mongo.UserBalanceHistories, ubhFilter,
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
            }, "userbalancehistories", ct);
        pulled += ubhPulled;

        if (pulled > 0)
            _logger.LogInformation("Pull: {UserCount} users, {UmCount} um, {WrCount} wr, {UbhCount} ubh",
                userPulled, umPulled, wrPulled, ubhPulled);
        _totalPulled += pulled;
    }

    private async Task<List<T>> PullWithFlexibleIds<T>(
        IMongoCollection<T> collection,
        FilterDefinition<T> filter,
        string collectionName,
        CancellationToken ct) where T : class, new()
    {
        var bsonColl = collection.Database.GetCollection<BsonDocument>(
            collection.CollectionNamespace.CollectionName);
        var bsonDocs = await bsonColl.Find(new BsonDocument()).ToListAsync(ct);

        var result = new List<T>();
        foreach (var bson in bsonDocs)
        {
            try
            {
                var idElement = bson["_id"];
                if (idElement.IsInt32 || idElement.IsInt64 || idElement.IsDouble)
                {
                    var doc = BsonSerializer.Deserialize<T>(bson);
                    result.Add(doc);
                }
                else
                {
                    _logger.LogDebug("Skipping {Collection} doc with non-numeric _id type: {Type}", collectionName, idElement.BsonType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Skipping unconvertible {Collection} doc: {Error}", collectionName, ex.Message);
            }
        }
        return result;
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
            List<T>? mongoDocs = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    mongoDocs = await collection.Find(filter).ToListAsync(ct);
                    break;
                }
                catch (MongoConnectionException) when (attempt == 1 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }
                catch (System.IO.IOException) when (attempt == 1 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }
                catch (System.Net.Sockets.SocketException) when (attempt == 1 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                }
                catch (Exception ex) when (attempt == 1 && !ct.IsCancellationRequested
                    && (ex.Message.Contains("Int64") || ex.Message.Contains("ObjectId") || ex is FormatException))
                {
                    _logger.LogWarning("Pull {Collection}: mixed _id types detected, using tolerant read", collectionName);
                    mongoDocs = await PullWithFlexibleIds(collection, filter, collectionName, ct);
                    break;
                }
            }

            if (mongoDocs == null || mongoDocs.Count == 0) return 0;

            var ids = new HashSet<long>(mongoDocs.Select(getId));
            var idList = ids.ToList();

            var localDocs = new List<T>();
            foreach (var batch in idList.Chunk(500))
            {
                var batchList = batch.ToList();
                var param = Expression.Parameter(typeof(T), "x");
                var prop = Expression.Property(param, "Id");
                var convertedProp = Expression.Convert(prop, typeof(long));
                var containsMethod = typeof(List<long>).GetMethod("Contains", new[] { typeof(long) })!;
                var call = Expression.Call(Expression.Constant(batchList), containsMethod, convertedProp);
                var predicate = Expression.Lambda<Func<T, bool>>(call, param);

                var batchDocs = await db.Set<T>().IgnoreQueryFilters().Where(predicate).ToListAsync(ct);
                localDocs.AddRange(batchDocs);
            }

            var localMap = new Dictionary<long, T>();
            foreach (var local in localDocs)
            {
                var id = getId(local);
                localMap[id] = local;
            }

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
                    var existingEntry = db.ChangeTracker.Entries<T>().FirstOrDefault(e => getId(e.Entity) == id);
                    if (existingEntry != null)
                    {
                        updateFields(existingEntry.Entity, m);
                        localMap[id] = existingEntry.Entity;
                    }
                    else
                    {
                        db.Set<T>().Add(m);
                        localMap[id] = m;
                    }
                }
                count++;
            }
            await SafeSaveAsync(db, collectionName, ct);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Pull from {Collection} skipped ({Error}) — will retry next cycle", collectionName, ex.Message);
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
            _logger.LogWarning(ex, "Sync pull: batch save failed for {Collection} — retrying individually", collection);
            var pendingEntries = db.ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified)
                .Select(e => e.Entity)
                .ToList();
            db.ChangeTracker.Clear();

            foreach (var entity in pendingEntries)
            {
                try
                {
                    db.Add(entity);
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                }
                catch (Exception ex2)
                {
                    _logger.LogWarning(ex2, "Individual save failed for {Collection} entity {EntityType}", collection, entity.GetType().Name);
                    db.ChangeTracker.Clear();
                }
            }
        }
    }
}
