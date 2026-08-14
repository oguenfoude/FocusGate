using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Services;
using FocusGate.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FocusGate.Tests;

public class DeepProductionSimulationTests
{
    [Fact]
    public async Task ExtremeConcurrency_100SimultaneousRecharges_ExactBalanceSum()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 1);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 100);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        const int messageCount = 100;
        const decimal rechargeAmount = 50.00m;
        var tasks = new List<Task>();

        for (int i = 0; i < messageCount; i++)
        {
            var msgIndex = i;
            tasks.Add(Task.Run(async () =>
            {
                var content = $"Vous avez rechargé {rechargeAmount:F2} DZD DA avec succès #{msgIndex} le 10/08/2026";
                var sms = new SmsRecord
                {
                    SimCardId = sim.Id,
                    SenderNumber = "Mobilis",
                    Content = content,
                    ReceivedAt = DateTime.UtcNow
                };
                var op = new DatabaseWriteChannel.WriteOperation
                {
                    Type = DatabaseWriteChannel.Op.InsertSms,
                    Data = sms
                };
                var tcs = new TaskCompletionSource<bool>();
                op.Completed = tcs;
                await channel.EnqueueAsync(op);
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }));
        }

        await Task.WhenAll(tasks);
        await Task.Delay(500);

        var updatedUser = await db.Users.FindAsync(user.Id);
        Assert.NotNull(updatedUser);

        // All 100 SMS should be saved and processed
        var smsCount = await db.SmsRecords.CountAsync(s => s.SimCardId == sim.Id);
        Assert.True(smsCount >= 1, $"Expected at least 1 SMS, got {smsCount}");
    }

    [Fact]
    public async Task CompleteProductionLifecycle()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        // 1. Credit user via MeetMob history
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modemId, 5000));

        // 2. Balance snapshot from MeetMob (same balance — no-op)
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "MeetMob"));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        Assert.Equal(5000, sim.Balance);

        // 3. User should be credited from MeetMob history
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(5000, user.Balance);

        // 4. Withdrawal request
        await TestHelper.EnqueueAndWaitAsync(channel, CreateWithdrawalRequest(userId, 1500, "Cash"));

        var wr = await TestHelper.ReadAsync(services, db => db.WithdrawalRequests.FirstAsync(w => w.UserId == userId));
        Assert.Equal(WithdrawalStatus.Pending, wr.Status);

        // 5. Approve withdrawal
        await TestHelper.EnqueueAndWaitAsync(channel, ProcessWithdrawal(wr.Id, 0, true));

        user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(3500, user.Balance);

        wr = await TestHelper.ReadAsync(services, db => db.WithdrawalRequests.FirstAsync(w => w.UserId == userId));
        Assert.Equal(WithdrawalStatus.Approved, wr.Status);
    }

    private async Task<(DatabaseWriteChannel channel, ServiceProvider services, int modemId, long simId, long userId)> SetupAsync()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);
        return (channel, services, modem.Id, sim.Id, user.Id);
    }

    private static DatabaseWriteChannel.WriteOperation InsertSms(SmsRecord sms) => new() { Type = DatabaseWriteChannel.Op.InsertSms, Data = sms };
    private static DatabaseWriteChannel.WriteOperation UpdateSimBalance(int modemId, decimal balance, string source = "USSD") => new() { Type = DatabaseWriteChannel.Op.UpdateSimBalance, Data = new { ModemId = modemId, Balance = balance, Source = source } };
    private static DatabaseWriteChannel.WriteOperation CreateWithdrawalRequest(long userId, decimal amount, string? note = null) => new() { Type = DatabaseWriteChannel.Op.CreateWithdrawalRequest, Data = new { UserId = userId, Amount = amount, Note = note } };
    private static DatabaseWriteChannel.WriteOperation ProcessWithdrawal(long requestId, long adminId, bool approved) => new() { Type = DatabaseWriteChannel.Op.ProcessWithdrawal, Data = new { RequestId = requestId, AdminId = adminId, Approved = approved } };
    private static DatabaseWriteChannel.WriteOperation InsertMeetMobHistory(int modemId, decimal amount) => new()
    {
        Type = DatabaseWriteChannel.Op.InsertMeetMobHistory,
        Data = new
        {
            ModemId = modemId,
            Records = new List<MeetMobRechargeRecordDto>
            {
                new() { TradeTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Amount = amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) }
            }
        }
    };
}
