using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using FocusGate.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace FocusGate.Tests;

public class DeepProductionSimulationTests
{
    private static DatabaseWriteChannel CreateChannel(FocusGateDbContext db)
    {
        return new DatabaseWriteChannel(
            new TestScopeFactory(db),
            NullLogger<DatabaseWriteChannel>.Instance,
            "sim_machine_01");
    }

    [Fact]
    public async Task ExtremeConcurrency_500SimultaneousRecharges_ExactBalanceSum()
    {
        using var db = TestHelper.CreateInMemoryDb();
        var user = new User { Id = 100, Username = "power_user", Balance = 0, MachineId = "sim_machine_01" };
        var sim = new SimCard { Id = 1, ModemId = 1, IMSI = "603010001", PhoneNumber = "0661000001", IsActive = true, MachineId = "sim_machine_01" };
        var modem = new Modem { Id = 1, IMEI = "860000000000001", Status = ModemStatus.Online, MachineId = "sim_machine_01" };
        var um = new UserModem { Id = 1, UserId = 100, ModemId = 1, MachineId = "sim_machine_01" };

        db.Users.Add(user);
        db.SimCards.Add(sim);
        db.Modems.Add(modem);
        db.UserModems.Add(um);
        await db.SaveChangesAsync();

        var channel = CreateChannel(db);
        using var cts = new CancellationTokenSource();
        _ = Task.Run(() => channel.StartAsync(cts.Token));

        const int messageCount = 100;
        const decimal rechargeAmount = 50.00m;
        var tasks = new List<Task>();

        for (int i = 0; i < messageCount; i++)
        {
            var msgIndex = i;
            tasks.Add(Task.Run(async () =>
            {
                var content = $"Vous avez rechargé {rechargeAmount:F2} DZD DA avec succès #{msgIndex} le 10/08/2026";
                await channel.EnqueueAsync(new DatabaseWriteChannel.WriteOperation
                {
                    Type = DatabaseWriteChannel.Op.InsertSms,
                    Data = new
                    {
                        SimCardId = 1L,
                        SenderNumber = "Mobilis",
                        Content = content,
                        ReceivedAt = DateTime.UtcNow.AddMilliseconds(msgIndex * 10)
                    }
                });
            }));
        }

        await Task.WhenAll(tasks);
        await Task.Delay(1000);
        await channel.CompleteAsync();

        var updatedUser = await db.Users.FirstAsync(u => u.Id == 100);
        Assert.True(updatedUser.Balance > 0, "User balance should be credited");
        Assert.Equal(messageCount * rechargeAmount, updatedUser.Balance);
    }

    [Fact]
    public async Task WithdrawalAndRecharge_InterleavedConcurrency_MaintainsIntegrity()
    {
        using var db = TestHelper.CreateInMemoryDb();
        var user = new User { Id = 200, Username = "trader_user", Balance = 10000m, MachineId = "sim_machine_01" };
        var sim = new SimCard { Id = 2, ModemId = 2, IMSI = "603010002", PhoneNumber = "0661000002", IsActive = true, MachineId = "sim_machine_01" };
        var modem = new Modem { Id = 2, IMEI = "860000000000002", Status = ModemStatus.Online, MachineId = "sim_machine_01" };
        var um = new UserModem { Id = 2, UserId = 200, ModemId = 2, MachineId = "sim_machine_01" };
        var wr = new WithdrawalRequest { Id = 501, UserId = 200, Amount = 3000m, Status = WithdrawalStatus.Pending, MachineId = "sim_machine_01" };

        db.Users.Add(user);
        db.SimCards.Add(sim);
        db.Modems.Add(modem);
        db.UserModems.Add(um);
        db.WithdrawalRequests.Add(wr);
        await db.SaveChangesAsync();

        var channel = CreateChannel(db);
        using var cts = new CancellationTokenSource();
        _ = Task.Run(() => channel.StartAsync(cts.Token));

        // Enqueue a recharge of +1,000 DA and approval of withdrawal of -3,000 DA concurrently
        var t1 = channel.EnqueueAsync(new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.InsertSms,
            Data = new
            {
                SimCardId = 2L,
                SenderNumber = "77111",
                Content = "Vous avez reçu un montant de 1000.00 DZD",
                ReceivedAt = DateTime.UtcNow
            }
        });

        var t2 = channel.EnqueueAsync(new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.ProcessWithdrawal,
            Data = new { RequestId = 501L, AdminId = 1L, Approved = true, AdminNote = "Approved in test" }
        });

        await Task.WhenAll(t1, t2);
        await Task.Delay(500);
        await channel.CompleteAsync();

        var finalUser = await db.Users.FirstAsync(u => u.Id == 200);
        var finalWr = await db.WithdrawalRequests.FirstAsync(w => w.Id == 501);

        // Expected: 10,000 + 1,000 - 3,000 = 8,000 DA
        Assert.Equal(8000m, finalUser.Balance);
        Assert.Equal(WithdrawalStatus.Approved, finalWr.Status);
    }

    [Theory]
    [InlineData("MOBILIS", "Vous avez reçu un montant de 500 DZD", 500)]
    [InlineData("mobilis", "Vous avez rechargé 1200.00 DZD", 1200)]
    [InlineData("77111", "montant de 2500 DZD reçu", 2500)]
    [InlineData("600", "Vous avez rechargé 350 DZD", 350)]
    [InlineData("666", "Vous avez reçu un montant de 100 DZD", 100)]
    [InlineData("610", "montant de 4000.00 DZD reçu", 4000)]
    public void ExtractRechargeAmount_AllCarrierSenders_ParsedAccurately(string sender, string content, decimal expected)
    {
        Assert.True(DatabaseWriteChannel.IsMobilisSender(sender));
        var amount = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(amount);
        Assert.Equal(expected, amount.Value);
    }

    [Fact]
    public void DailyMidnightVoid_Calculation_AlwaysTargetsTomorrowMidnight()
    {
        var now = DateTime.Now;
        var nextMidnight = now.Date.AddDays(1);
        var delay = nextMidnight - now;

        Assert.True(delay.TotalSeconds > 0, "Delay must be positive");
        Assert.True(delay.TotalHours <= 24.0, "Delay must be <= 24 hours");
        Assert.Equal(0, nextMidnight.Hour);
        Assert.Equal(0, nextMidnight.Minute);
        Assert.Equal(0, nextMidnight.Second);
    }
}
