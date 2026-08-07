using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using FocusGate.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusGate.Tests;

public static class TestHelper
{
    public static async Task<(FocusGateDbContext db, DatabaseWriteChannel channel, ServiceProvider services)>
        CreateInMemoryDatabaseWithChannelAsync()
    {
        var services = new ServiceCollection();

        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        services.AddDbContext<FocusGateDbContext>(options =>
            options.UseSqlite(connection));

        services.AddLogging(builder => builder.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<DatabaseWriteChannel>();

        // MachineId setter (required by ProcessQueueAsync)
        services.AddSingleton<Action<FocusGateDbContext>>(db => { db.MachineId = "test-machine"; });

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        var channel = provider.GetRequiredService<DatabaseWriteChannel>();
        channel.Start(CancellationToken.None);

        var dbInstance = provider.CreateScope().ServiceProvider.GetRequiredService<FocusGateDbContext>();
        return (dbInstance, channel, provider);
    }

    public static async Task<Modem> SeedModemAsync(FocusGateDbContext db, int? id = null, string? imei = null)
    {
        var modem = new Modem
        {
            Id = id ?? Random.Shared.Next(1, 10000),
            IMEI = imei ?? $"8645760{Random.Shared.Next(1000000, 9999999)}",
            Status = ModemStatus.Online,
            Brand = ModemBrand.Huawei,
            Model = "E3531",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Modems.Add(modem);
        await db.SaveChangesAsync();
        return modem;
    }

    public static async Task<SimCard> SeedSimCardAsync(FocusGateDbContext db, int modemId, decimal balance = 0, string? imsi = null, long phone = 0)
    {
        var sim = new SimCard
        {
            ModemId = modemId,
            IMSI = imsi ?? $"603019{Random.Shared.Next(100000, 999999)}",
            PhoneNumber = phone > 0 ? phone : Random.Shared.NextInt64(1000000000, 9999999999),
            Balance = balance,
            IsActive = true,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        db.SimCards.Add(sim);
        await db.SaveChangesAsync();
        return sim;
    }

    public static async Task<User> SeedUserAsync(FocusGateDbContext db, long? id = null, string? username = null, decimal balance = 0)
    {
        var user = new User
        {
            Id = id ?? Random.Shared.NextInt64(1000000000000000, 9999999999999999),
            Username = username ?? $"user_{Random.Shared.Next(1000, 9999)}",
            Password = "test123",
            DisplayName = "Test User",
            Role = UserRole.User,
            Balance = balance,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<UserModem> AssignUserToModemAsync(FocusGateDbContext db, long userId, int modemId)
    {
        var um = new UserModem
        {
            UserId = userId,
            ModemId = modemId,
            AssignedAt = DateTime.UtcNow
        };
        db.UserModems.Add(um);
        await db.SaveChangesAsync();
        return um;
    }

    public static SmsRecord CreateMobilisRechargeSms(long simCardId, decimal amount, string? extraText = null)
    {
        var now = DateTime.UtcNow;
        return new SmsRecord
        {
            SimCardId = simCardId,
            SenderNumber = "Mobilis",
            Content = $"Vous avez été rechargé de {amount}DA{extraText ?? ""}. Merci.",
            ReceivedAt = now
        };
    }

    public static SmsRecord CreateMobilisTransferSms(long simCardId, decimal amount)
    {
        var now = DateTime.UtcNow;
        return new SmsRecord
        {
            SimCardId = simCardId,
            SenderNumber = "77111",
            Content = $"montant de {amount}DA reçu du 0555123456. Votre solde: 3788,16DA.",
            ReceivedAt = now
        };
    }

    public static SmsRecord CreateMobilisSoldeSms(long simCardId, decimal balance)
    {
        var now = DateTime.UtcNow;
        var formatted = balance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return new SmsRecord
        {
            SimCardId = simCardId,
            SenderNumber = "Mobilis",
            Content = $"Sama, Solde {formatted}DA, Bonus internet 5Go 5,00Go valable au 08/08/2026",
            ReceivedAt = now
        };
    }

    public static SmsRecord CreateNonMobilisSms(long simCardId)
    {
        return new SmsRecord
        {
            SimCardId = simCardId,
            SenderNumber = "12345",
            Content = "Your verification code is 1234. Do not share.",
            ReceivedAt = DateTime.UtcNow
        };
    }

    public static async Task DrainChannelAsync(DatabaseWriteChannel channel)
    {
        await Task.Delay(500);
    }

    public static async Task EnqueueAndWaitAsync(DatabaseWriteChannel channel, DatabaseWriteChannel.WriteOperation op)
    {
        var tcs = new TaskCompletionSource<bool>();
        op.Completed = tcs;
        await channel.EnqueueAsync(op);
        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!result)
            throw new InvalidOperationException($"WriteChannel operation {op.Type} returned false — operation failed silently");
    }

    public static async Task<bool> EnqueueAndReturnResultAsync(DatabaseWriteChannel channel, DatabaseWriteChannel.WriteOperation op)
    {
        var tcs = new TaskCompletionSource<bool>();
        op.Completed = tcs;
        await channel.EnqueueAsync(op);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public static async Task<T> ReadAsync<T>(ServiceProvider services, Func<FocusGateDbContext, Task<T>> query)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
        return await query(db);
    }

    public static async Task<List<T>> ReadAllAsync<T>(ServiceProvider services, Func<FocusGateDbContext, IQueryable<T>> query)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
        return await query(db).ToListAsync();
    }
}
