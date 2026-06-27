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
    public static IServiceCollection AddFocusGate(this IServiceCollection services, IConfiguration config, string? dataDir = null)
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

        var flatConfig = ReadFlatConfig(configPath);

        var mongoUri = flatConfig.GetValueOrDefault("mongodb.uri")
            ?? Environment.GetEnvironmentVariable("MONGODB_URI")
            ?? "";

        if (string.IsNullOrEmpty(mongoUri))
        {
            Console.WriteLine("[!] MongoDB URI not found in config — cloud sync disabled");
        }
        else if (mongoUri.Contains("user:password"))
        {
            Console.WriteLine("[!] MongoDB URI is placeholder — run 'setmongo <uri>' to set real URI");
        }
        else
        {
            if (!mongoUri.Contains("connectTimeoutMS"))
            {
                var separator = mongoUri.Contains('?') ? '&' : '?';
                mongoUri += $"{separator}connectTimeoutMS=5000&serverSelectionTimeoutMS=5000";
            }
        }

        var mongoDb = flatConfig.GetValueOrDefault("mongodb.database") ?? config["mongodb:database"] ?? "focusgate";
        var syncStr = flatConfig.GetValueOrDefault("sync.interval_seconds") ?? config["sync:interval_seconds"];
        var syncInterval = int.TryParse(syncStr, out var si) ? si : 30;

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<FocusGateMongoClient>>();
            if (string.IsNullOrEmpty(mongoUri) || mongoUri.Contains("user:password"))
                return new FocusGateMongoClient(null, mongoDb, logger);
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
        services.AddHostedService<ConsoleCommandHandler>();
        services.AddHostedService<RestartService>();

        return services;
    }

    public static IServiceCollection AddFocusGateDashboard(this IServiceCollection services, IConfiguration config, string? dataDir = null)
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

        return services;
    }

    private static Dictionary<string, string> ReadFlatConfig(string configPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    result[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
        catch { }
        return result;
    }
}
