using FocusGate.Core.Interfaces;
using FocusGate.Infrastructure.Data;
using FocusGate.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, string? dataDir = null)
    {
        var dir = dataDir ?? FocusGate.Core.Services.PathService.DataDirectory;
        Directory.CreateDirectory(dir);

        var dbPath = Path.Combine(dir, "focusgate.db");
        var connectionString = $"Data Source={dbPath}";

        var machineId = config["machine.id"] ?? "";

        services.AddSingleton<MachineInfoService>();
        services.AddSingleton<DatabaseWriteChannel>();
        var configPath = Path.Combine(dir, "config.json");
        services.AddSingleton<IConfigProvider>(new JsonConfigProvider(configPath));

        services.AddDbContext<FocusGateDbContext>(options =>
        {
            options.UseSqlite(connectionString);
        });

        services.AddSingleton(sp =>
        {
            var machine = sp.GetRequiredService<MachineInfoService>();
            var id = string.IsNullOrEmpty(machineId) ? machine.MachineId : machineId;
            return (Action<FocusGateDbContext>)(ctx => ctx.MachineId = id);
        });

        var mongoUri = config["mongodb.uri"]
            ?? Environment.GetEnvironmentVariable("MONGODB_URI")
            ?? "mongodb+srv://admin:admin@cluster0.ldndrwe.mongodb.net/?appName=Cluster0";

        if (!mongoUri.Contains("connectTimeoutMS"))
        {
            var separator = mongoUri.Contains('?') ? '&' : '?';
            mongoUri += $"{separator}connectTimeoutMS=5000&serverSelectionTimeoutMS=5000";
        }
        var mongoDb = config["mongodb.database"] ?? "focusgate";
        var syncInterval = int.TryParse(config["sync.interval_seconds"], out var si) ? si : 30;

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<FocusGateMongoClient>>();
            return new FocusGateMongoClient(mongoUri, mongoDb, logger);
        });

        services.AddSingleton(sp =>
        {
            var mongo = sp.GetRequiredService<FocusGateMongoClient>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var logger = sp.GetRequiredService<ILogger<MongoSyncService>>();
            var machine = sp.GetRequiredService<MachineInfoService>();
            var id = string.IsNullOrEmpty(machineId) ? machine.MachineId : machineId;
            return new MongoSyncService(mongo, scopeFactory, logger, id, syncInterval);
        });

        services.AddHostedService(sp => sp.GetRequiredService<MongoSyncService>());

        return services;
    }
}
