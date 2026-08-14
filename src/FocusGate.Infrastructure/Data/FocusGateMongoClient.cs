using System.Security.Authentication;
using FocusGate.Core.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace FocusGate.Infrastructure.Data;

public class FocusGateMongoClient
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<FocusGateMongoClient> _logger;

    public IMongoCollection<Modem>             Modems              => _db?.GetCollection<Modem>("modems") ?? throw new InvalidOperationException("MongoDB not connected");
    public IMongoCollection<SimCard>           SimCards            => _db?.GetCollection<SimCard>("simcards") ?? throw new InvalidOperationException("MongoDB not connected");
    public IMongoCollection<SmsRecord>         SmsRecords          => _db?.GetCollection<SmsRecord>("smsrecords") ?? throw new InvalidOperationException("MongoDB not connected");
    public IMongoCollection<BalanceHistory>    BalanceHistories    => _db?.GetCollection<BalanceHistory>("balancehistories") ?? throw new InvalidOperationException("MongoDB not connected");
    public IMongoCollection<User>              Users               => _db?.GetCollection<User>("users") ?? throw new InvalidOperationException("MongoDB not connected");
    public IMongoCollection<UserModem>         UserModems          => _db?.GetCollection<UserModem>("usermodems") ?? throw new InvalidOperationException("MongoDB not connected");
    public IMongoCollection<WithdrawalRequest> WithdrawalRequests  => _db?.GetCollection<WithdrawalRequest>("withdrawalrequests") ?? throw new InvalidOperationException("MongoDB not connected");
    public IMongoCollection<UserBalanceHistory> UserBalanceHistories => _db?.GetCollection<UserBalanceHistory>("userbalancehistories") ?? throw new InvalidOperationException("MongoDB not connected");

    public bool IsConnected { get; internal set; }

    public FocusGateMongoClient(string? connectionString, string databaseName, ILogger<FocusGateMongoClient> logger)
    {
        _logger = logger;

        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogInformation("MongoDB URI empty — cloud sync disabled");
            _db = null!;
            IsConnected = false;
            return;
        }

        var pack = new ConventionPack { new CamelCaseElementNameConvention(), new IgnoreExtraElementsConvention(true) };
        ConventionRegistry.Register("FocusGate", pack, _ => true);

        RegisterClassMaps();

        try
        {
            _logger.LogInformation("MongoDB connecting to {Host}...", ExtractHost(connectionString));
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ReadPreference = ReadPreference.SecondaryPreferred;
            settings.RetryWrites = true;
            settings.RetryReads = true;
            settings.ConnectTimeout = TimeSpan.FromSeconds(10);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(15);
            settings.SocketTimeout = TimeSpan.FromSeconds(15);
            settings.HeartbeatInterval = TimeSpan.FromSeconds(10);
            settings.MaxConnectionIdleTime = TimeSpan.FromSeconds(20);
            settings.MaxConnectionLifeTime = TimeSpan.FromMinutes(5);
            settings.SslSettings = new SslSettings
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };
            settings.ServerApi = new ServerApi(ServerApiVersion.V1, strict: false);
            settings.ApplicationName = "FocusGate";
            var client = new MongoClient(settings);
            _db = client.GetDatabase(databaseName);
            IsConnected = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MongoDB client creation failed: {Error}", ex.Message);
            _db = null!;
            IsConnected = false;
        }
    }

    private static string ExtractHost(string connectionString)
    {
        try
        {
            var uri = new Uri(connectionString);
            return uri.Host;
        }
        catch { return connectionString.Length > 40 ? connectionString[..40] + "..." : connectionString; }
    }

    private static void RegisterClassMaps()
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(Modem))) return;

        BsonClassMap.RegisterClassMap<Modem>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(m => m.Id);
            cm.UnmapMember(m => m.SimCards);
        });

        BsonClassMap.RegisterClassMap<SimCard>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(s => s.Id);
            cm.UnmapMember(s => s.Modem);
            cm.UnmapMember(s => s.SmsRecords);
        });

        BsonClassMap.RegisterClassMap<SmsRecord>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(s => s.Id);
            cm.UnmapMember(s => s.SimCard);
        });

        BsonClassMap.RegisterClassMap<BalanceHistory>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(b => b.Id);
            cm.UnmapMember(b => b.SimCard);
            cm.UnmapMember(b => b.Modem);
            cm.UnmapMember(b => b.User);
        });

        BsonClassMap.RegisterClassMap<User>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(u => u.Id);
            cm.UnmapMember(u => u.UserModems);
            cm.UnmapMember(u => u.BalanceHistories);
            cm.UnmapMember(u => u.WithdrawalRequests);
            cm.UnmapMember(u => u.UserBalanceHistories);
        });

        BsonClassMap.RegisterClassMap<UserModem>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(um => um.Id);
            cm.UnmapMember(um => um.User);
            cm.UnmapMember(um => um.Modem);
        });

        BsonClassMap.RegisterClassMap<WithdrawalRequest>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(w => w.Id);
            cm.UnmapMember(w => w.User);
            cm.UnmapMember(w => w.ProcessedByAdmin);
        });

        BsonClassMap.RegisterClassMap<UserBalanceHistory>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(ub => ub.Id);
            cm.UnmapMember(ub => ub.User);
            cm.UnmapMember(ub => ub.SimCard);
        });
    }

    public async Task<bool> UpsertAsync<T>(IMongoCollection<T> collection, T document, CancellationToken ct = default) where T : class
    {
        if (!IsConnected) return false;
        try
        {
            var idProp = typeof(T).GetProperty("Id");
            if (idProp == null) return false;
            var id = idProp.GetValue(document);
            if (id == null) return false;

            var filter = Builders<T>.Filter.Eq("_id", id);
            var options = new ReplaceOptions { IsUpsert = true };
            await collection.ReplaceOneAsync(filter, document, options, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MongoDB upsert failed for {Type}", typeof(T).Name);
            return false;
        }
    }

    public async Task<bool> WriteSmsAsync(SmsRecord sms, CancellationToken ct = default)
    {
        if (!IsConnected) return false;
        try
        {
            await SmsRecords.InsertOneAsync(sms, cancellationToken: ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MongoDB SMS write failed for {Id}", sms.Id);
            return false;
        }
    }

    public async Task<int> UpsertManyAsync<T>(IMongoCollection<T> collection, IEnumerable<T> documents, CancellationToken ct = default) where T : class
    {
        if (!IsConnected) return 0;
        var list = documents.ToList();
        if (list.Count == 0) return 0;
        try
        {
            var idProp = typeof(T).GetProperty("Id");
            if (idProp == null) return 0;

            var writes = new List<WriteModel<T>>(list.Count);
            foreach (var doc in list)
            {
                var id = idProp.GetValue(doc);
                if (id == null) continue;
                var filter = Builders<T>.Filter.Eq("_id", id);
                writes.Add(new ReplaceOneModel<T>(filter, doc) { IsUpsert = true });
            }

            if (writes.Count == 0) return 0;
            var result = await collection.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
            return (int)(result.Upserts?.Count ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MongoDB bulk upsert failed for {Type} ({Count} docs)", typeof(T).Name, list.Count);
            return 0;
        }
    }

    public async Task<int> InsertManyAsync<T>(IMongoCollection<T> collection, IEnumerable<T> documents, CancellationToken ct = default) where T : class
    {
        if (!IsConnected) return 0;
        var list = documents.ToList();
        if (list.Count == 0) return 0;
        try
        {
            await collection.InsertManyAsync(list, new InsertManyOptions { IsOrdered = false }, ct);
            return list.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MongoDB bulk insert failed for {Type} ({Count} docs)", typeof(T).Name, list.Count);
            return 0;
        }
    }

    public async Task<bool> TestConnectionAsync()
    {
        if (_db == null)
        {
            IsConnected = false;
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token);
            IsConnected = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("MongoDB ping timed out after 10s");
            IsConnected = false;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MongoDB ping failed: {Error}", ex.Message);
            IsConnected = false;
            return false;
        }
    }
}
