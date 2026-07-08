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
            var client = new MongoClient(connectionString);
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

    public async Task<bool> TestConnectionAsync()
    {
        if (_db == null)
        {
            IsConnected = false;
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token);
            IsConnected = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("MongoDB ping timed out after 20s");
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
