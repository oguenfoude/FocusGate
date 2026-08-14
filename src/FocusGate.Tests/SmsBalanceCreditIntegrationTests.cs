using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Services;
using FocusGate.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FocusGate.Tests;

public class SmsBalanceCreditIntegrationTests
{
    private async Task<(DatabaseWriteChannel channel, ServiceProvider services, int modemId, long simCardId, long userId)> SetupAsync()
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
    public async Task RechargeSms_Mobilis_SavesToDb()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();
        var sms = TestHelper.CreateMobilisRechargeSms(simId, 2000);

        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(sms));

        var saved = await TestHelper.ReadAsync(services, db => db.SmsRecords.FirstAsync(s => s.SimCardId == simId));
        Assert.Equal("Mobilis", saved.SenderNumber);
        Assert.Contains("2000", saved.Content);
    }

    [Fact]
    public async Task RechargeSms_77111_SavesToDb()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();
        var sms = TestHelper.CreateMobilisTransferSms(simId, 1500);

        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(sms));

        var saved = await TestHelper.ReadAsync(services, db => db.SmsRecords.FirstAsync(s => s.SenderNumber == "77111"));
        Assert.Equal("77111", saved.SenderNumber);
    }

    [Fact]
    public async Task RechargeSms_InstantCredit_CreditsUser()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        // SMS detection does NOT credit user wallet
        var sms = TestHelper.CreateMobilisTransferSms(simId, 5000);
        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(sms));

        var userAfterSms = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(0, userAfterSms.Balance);

        // MeetMob history DOES credit user wallet
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modemId, 5000));

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(5000, user.Balance);
    }

    [Fact]
    public async Task SoldeSms_UpdatesSimBalance_NoCredit()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(TestHelper.CreateMobilisSoldeSms(simId, 5000)));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(5000, sim.Balance);
        Assert.Equal(0, user.Balance); // Solde SMS only updates SIM balance, not user wallet
    }

    [Fact]
    public async Task UpdateSimBalance_Increase_RecordsBalanceHistory()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "MeetMob"));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.ModemId == modemId && s.IsActive));
        Assert.Equal(5000, sim.Balance);

        var history = await TestHelper.ReadAsync(services, db => db.BalanceHistories.FirstAsync(b => b.SimCardId == simId));
        Assert.Equal(5000, history.Balance);
        Assert.Equal(BalanceSource.MeetMob, history.Source);
    }

    [Fact]
    public async Task UpdateSimBalance_Decrease_CreatesBalanceHistory()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 10000, "Test"));
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "Test"));

        var count = await TestHelper.ReadAsync(services, db => db.BalanceHistories.CountAsync(b => b.SimCardId == simId));
        Assert.Equal(2, count);

        var decrease = await TestHelper.ReadAsync(services, db => db.BalanceHistories
            .Where(b => b.SimCardId == simId && b.Balance < b.PreviousBalance)
            .OrderByDescending(b => b.RecordedAt).FirstAsync());
        Assert.Equal(5000, decrease.Balance);
        Assert.Equal(10000, decrease.PreviousBalance);
    }

    [Fact]
    public async Task WithdrawalApproved_DeductsFromUser()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        // Credit user via MeetMob history
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modemId, 10000));

        await TestHelper.EnqueueAndWaitAsync(channel, CreateWithdrawalRequest(userId, 3000, "Cash out"));

        var wr = await TestHelper.ReadAsync(services, db => db.WithdrawalRequests.FirstAsync(w => w.UserId == userId));
        await TestHelper.EnqueueAndWaitAsync(channel, ProcessWithdrawal(wr.Id, 0, true));

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(7000, user.Balance);
    }

    [Fact]
    public async Task WithdrawalRejected_NoDeduction()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        // Credit user via MeetMob history
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modemId, 10000));

        await TestHelper.EnqueueAndWaitAsync(channel, CreateWithdrawalRequest(userId, 3000, "Cash out"));

        var wr = await TestHelper.ReadAsync(services, db => db.WithdrawalRequests.FirstAsync(w => w.UserId == userId));
        await TestHelper.EnqueueAndWaitAsync(channel, ProcessWithdrawal(wr.Id, 0, false));

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(10000, user.Balance);
    }

    [Fact]
    public async Task BalanceHistory_HasCorrectFields()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "MeetMob"));

        var history = await TestHelper.ReadAsync(services, db => db.BalanceHistories.FirstAsync(b => b.SimCardId == simId));
        Assert.Equal(simId, history.SimCardId);
        Assert.Equal(5000, history.Balance);
        Assert.Equal(BalanceSource.MeetMob, history.Source);
        Assert.True(history.RecordedAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task UserBalanceHistory_CreatedOnRecharge()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        // Credit user via MeetMob history
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modemId, 5000));

        var history = await TestHelper.ReadAsync(services, db => db.UserBalanceHistories.FirstAsync(h => h.UserId == userId));
        Assert.Equal(5000, history.Amount);
        Assert.Equal(userId, history.UserId);
    }

    [Fact]
    public async Task EuropeanNumberFormat_Recharge_CreditsUser()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        // Credit user via MeetMob history
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modemId, 3788.16m));

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(3788.16m, user.Balance);
    }

    [Fact]
    public async Task ZeroBalance_FirstCheck_UpdatesSim_NoCredit()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "MeetMob"));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(5000, sim.Balance);
        Assert.Equal(0, user.Balance); // No credit from balance snapshot
    }
}
