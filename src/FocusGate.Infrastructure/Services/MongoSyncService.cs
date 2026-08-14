using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
