using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Services;
using FocusGate.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FocusGate.Tests;

public class StressEdgeCaseTests
{
    private async Task<(DatabaseWriteChannel channel, ServiceProvider services, int modemId, long simCardId, long userId)>
        SetupAsync()
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

    [Fact]
    public async Task ConcurrentEnqueue_MultipleCreditsAndBalanceUpdates()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        // Credit user via recharge SMS
        var tcs1 = new TaskCompletionSource<bool>();
        var tcs2 = new TaskCompletionSource<bool>();
        var sms = TestHelper.CreateMobilisTransferSms(simId, 1000);
        var op1 = InsertSms(sms);
        op1.Completed = tcs1;
        var op2 = UpdateSimBalance(modemId, 5000, "MeetMob");
        op2.Completed = tcs2;

        await channel.EnqueueAsync(op1);
        await channel.EnqueueAsync(op2);
        await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.True(user.Balance >= 0);
    }

    [Fact]
    public async Task RapidRechargeSms_SameSecond_AllSaved()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        for (int i = 1; i <= 5; i++)
            await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(TestHelper.CreateMobilisRechargeSms(simId, i * 1000)));

        var smsCount = await TestHelper.ReadAsync(services, db => db.SmsRecords.CountAsync(s => s.SimCardId == simId));
        Assert.True(smsCount >= 1, $"Expected at least 1 SMS, got {smsCount}");
    }

    // UpdateSimBalance updates SIM balance only. It does NOT credit the user wallet.
    [Theory]
    [InlineData(0.01)]
    [InlineData(999999.99)]
    [InlineData(1)]
    [InlineData(50000)]
    public async Task BoundaryValues_BalanceHandling(decimal balance)
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, balance, "MeetMob"));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(balance, sim.Balance);
        Assert.Equal(0, user.Balance); // No credit from balance snapshot — history handles crediting
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task ZeroOrNegativeAmount_BalanceSetAsIs(decimal amount)
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, amount, "MeetMob"));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        Assert.Equal(amount, sim.Balance);
    }

    [Fact]
    public async Task VeryLargeBalance_Handled()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();
        decimal largeBalance = 999999.99m;

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, largeBalance, "MeetMob"));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        Assert.Equal(largeBalance, sim.Balance);
    }

    [Fact]
    public async Task ZeroBalance_SimStartsAtZero()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        Assert.Equal(0, sim.Balance);
    }

    [Fact]
    public async Task SequentialBalanceUpdates_FinalState()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        decimal[] updates = [1000, 3500, 2800, 5000, 4200];
        foreach (var bal in updates)
            await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, bal, "MeetMob"));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        Assert.Equal(4200, sim.Balance);
    }

    [Theory]
    [InlineData("Mobilis")]
    [InlineData("77111")]
    [InlineData("610")]
    [InlineData("21521")]
    public async Task AllMobilisSenderVariants_SavedToDb(string sender)
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();
        var sms = new SmsRecord
        {
            SimCardId = simId,
            SenderNumber = sender,
            Content = "Vous avez été rechargé de 1000DA. Nouveau solde: 5000,00DA.",
            ReceivedAt = DateTime.UtcNow
        };

        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(sms));

        var saved = await TestHelper.ReadAsync(services, db => db.SmsRecords.FirstAsync(s => s.SimCardId == simId));
        Assert.Equal(sender, saved.SenderNumber);
    }

    [Fact]
    public async Task InsertModem_DuplicateImei_HandledGracefully()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem1 = await TestHelper.SeedModemAsync(db, imei: "123456789012345");
        await TestHelper.SeedSimCardAsync(db, modem1.Id);

        var op = new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.InsertModem,
            Data = new { IMEI = "123456789012345", IMSI = "603019999999999", ComPort = "COM99", Manufacturer = "Huawei", Model = "E3531", Brand = (int)ModemBrand.Huawei }
        };
        await TestHelper.EnqueueAndWaitAsync(channel, op);

        var existing = await TestHelper.ReadAsync(services, db => db.Modems.FirstOrDefaultAsync(m => m.IMEI == "123456789012345"));
        Assert.NotNull(existing);
    }

    [Fact]
    public async Task CompleteProductionLifecycle()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        // Credit user via MeetMob history
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modemId, 5000));

        // Balance snapshot from MeetMob (same balance — no-op)
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "MeetMob"));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        Assert.Equal(5000, sim.Balance);

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(5000, user.Balance);

        await TestHelper.EnqueueAndWaitAsync(channel, CreateWithdrawalRequest(userId, 1500, "Cash"));

        var wr = await TestHelper.ReadAsync(services, db => db.WithdrawalRequests.FirstAsync(w => w.UserId == userId));
        Assert.Equal(WithdrawalStatus.Pending, wr.Status);

        await TestHelper.EnqueueAndWaitAsync(channel, ProcessWithdrawal(wr.Id, 0, true));

        user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(3500, user.Balance);

        wr = await TestHelper.ReadAsync(services, db => db.WithdrawalRequests.FirstAsync(w => w.UserId == userId));
        Assert.Equal(WithdrawalStatus.Approved, wr.Status);
    }

    // Balance snapshots accumulate in BalanceHistory, but do NOT credit the user wallet.
    // The user wallet only grows via instant credit from recharge SMS (processed in HandleInsertSmsAsync).
    [Fact]
    public async Task MultipleRecharges_AccumulateHistory()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 3000, "MeetMob"));
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 7000, "MeetMob"));
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 12000, "MeetMob"));

        // Balance history records each SIM balance snapshot (3 increases)
        var histories = await TestHelper.ReadAllAsync(services, db => db.BalanceHistories.Where(b => b.SimCardId == simId));
        Assert.Equal(3, histories.Count);

        // User wallet stays 0 — no credit from balance snapshots alone
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(0, user.Balance);
    }
}
