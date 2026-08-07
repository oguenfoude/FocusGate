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
    private static DatabaseWriteChannel.WriteOperation UpdateSimBalanceFromSms(int modemId, decimal balance) => new() { Type = DatabaseWriteChannel.Op.UpdateSimBalanceFromSms, Data = new { ModemId = modemId, Balance = balance } };
    private static DatabaseWriteChannel.WriteOperation CreditUserFromRechargeSms(int modemId, decimal amount) => new() { Type = DatabaseWriteChannel.Op.CreditUserFromRechargeSms, Data = new { ModemId = modemId, RechargeAmount = amount } };
    private static DatabaseWriteChannel.WriteOperation CreateWithdrawalRequest(long userId, decimal amount, string? note = null) => new() { Type = DatabaseWriteChannel.Op.CreateWithdrawalRequest, Data = new { UserId = userId, Amount = amount, Note = note } };
    private static DatabaseWriteChannel.WriteOperation ProcessWithdrawal(long requestId, long adminId, bool approved) => new() { Type = DatabaseWriteChannel.Op.ProcessWithdrawal, Data = new { RequestId = requestId, AdminId = adminId, Approved = approved } };

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

        var saved = await TestHelper.ReadAsync(services, db => db.SmsRecords.FirstAsync(s => s.SimCardId == simId));
        Assert.Equal("77111", saved.SenderNumber);
        Assert.Contains("1500", saved.Content);
    }

    [Fact]
    public async Task RechargeSms_610_SavesToDb()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();
        var sms = new SmsRecord
        {
            SimCardId = simId,
            SenderNumber = "610",
            Content = "Vous avez été rechargé de 500DA. Nouveau solde: 1500,00DA.",
            ReceivedAt = DateTime.UtcNow
        };

        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(sms));

        var saved = await TestHelper.ReadAsync(services, db => db.SmsRecords.FirstAsync(s => s.SenderNumber == "610"));
        Assert.Equal("610", saved.SenderNumber);
    }

    [Fact]
    public async Task SoldeSms_Increase_CreditsUser()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "Test"));
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalanceFromSms(modemId, 7500));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(7500, sim.Balance);
        Assert.Equal(7500, user.Balance);
    }

    [Fact]
    public async Task SoldeSms_NoPending_NoCredit()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "Test"));
        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(TestHelper.CreateMobilisSoldeSms(simId, 5000)));

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(5000, user.Balance);
    }

    [Fact]
    public async Task BalanceDecrease_NoCredit()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 10000, "Test"));
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "Test"));

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(10000, user.Balance);
    }

    [Fact]
    public async Task BalanceUnchanged_NoCredit()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "Test"));
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "Test"));

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(5000, user.Balance);
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
    public async Task UpdateSimBalance_Decrease_NoBalanceHistory()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 10000, "Test"));
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "Test"));

        var count = await TestHelper.ReadAsync(services, db => db.BalanceHistories.CountAsync(b => b.SimCardId == simId));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UpdateSimBalance_Unchanged_NoBalanceHistory()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "Test"));
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "Test"));

        var count = await TestHelper.ReadAsync(services, db => db.BalanceHistories.CountAsync(b => b.SimCardId == simId));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UpdateSimBalanceFromSms_Increase_CreditsUserAndRecordsHistory()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalanceFromSms(modemId, 3000));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        var history = await TestHelper.ReadAsync(services, db => db.BalanceHistories.FirstAsync(b => b.SimCardId == simId));

        Assert.Equal(3000, sim.Balance);
        Assert.Equal(3000, user.Balance);
        Assert.Equal(BalanceSource.SMS, history.Source);
    }

    [Fact]
    public async Task UpdateSimBalanceFromSms_Decrease_NoCredit()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalanceFromSms(modemId, 5000));
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalanceFromSms(modemId, 3000));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));

        Assert.Equal(3000, sim.Balance);
        Assert.Equal(5000, user.Balance);
    }

    [Fact]
    public async Task CreditUserFromRechargeSms_CreditsWallet()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, CreditUserFromRechargeSms(modemId, 2500));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(2500, sim.Balance);
        Assert.Equal(2500, user.Balance);
    }

    [Fact]
    public async Task CreditUserFromRechargeSms_CreatesHistory()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, CreditUserFromRechargeSms(modemId, 1000));

        var history = await TestHelper.ReadAsync(services, db => db.BalanceHistories.FirstAsync(b => b.SimCardId == simId));
        Assert.Equal(BalanceSource.SMS, history.Source);
    }

    [Fact]
    public async Task SmsDedup_SameSimSenderContentWithin2Min_NotDuplicated()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();
        var sms1 = TestHelper.CreateMobilisRechargeSms(simId, 1000);
        var sms2 = TestHelper.CreateMobilisRechargeSms(simId, 1000);

        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(sms1));
        var dedupResult = await TestHelper.EnqueueAndReturnResultAsync(channel, InsertSms(sms2));
        Assert.False(dedupResult, "Duplicate SMS should be rejected");

        var count = await TestHelper.ReadAsync(services, db => db.SmsRecords.CountAsync(s => s.SimCardId == simId));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task NonMobilisSms_SavedToDb_WithSender()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();
        var sms = TestHelper.CreateNonMobilisSms(simId);

        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(sms));

        var saved = await TestHelper.ReadAsync(services, db => db.SmsRecords.FirstAsync(s => s.SimCardId == simId));
        Assert.Equal("12345", saved.SenderNumber);
        Assert.Contains("verification code", saved.Content);
    }

    [Fact]
    public async Task MultipleModems_IndependentBalanceTracking()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem1 = await TestHelper.SeedModemAsync(db);
        var modem2 = await TestHelper.SeedModemAsync(db);
        var sim1 = await TestHelper.SeedSimCardAsync(db, modem1.Id);
        var sim2 = await TestHelper.SeedSimCardAsync(db, modem2.Id);
        var user = await TestHelper.SeedUserAsync(db);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem1.Id);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem2.Id);

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modem1.Id, 5000, "MeetMob"));
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modem2.Id, 8000, "MeetMob"));

        var s1 = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == sim1.Id));
        var s2 = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == sim2.Id));
        Assert.Equal(5000, s1.Balance);
        Assert.Equal(8000, s2.Balance);

        var histories = await TestHelper.ReadAllAsync(services, db => db.BalanceHistories);
        Assert.Equal(2, histories.Count);
    }

    [Fact]
    public async Task WithdrawalApproved_DeductsFromUserBalance()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, CreditUserFromRechargeSms(modemId, 10000));
        await TestHelper.EnqueueAndWaitAsync(channel, CreateWithdrawalRequest(userId, 3000, "Cash out"));

        var wr = await TestHelper.ReadAsync(services, db => db.WithdrawalRequests.FirstAsync(w => w.UserId == userId));
        await TestHelper.EnqueueAndWaitAsync(channel, ProcessWithdrawal(wr.Id, 0, true));

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(7000, user.Balance);

        wr = await TestHelper.ReadAsync(services, db => db.WithdrawalRequests.FirstAsync(w => w.UserId == userId));
        Assert.Equal(WithdrawalStatus.Approved, wr.Status);
    }

    [Fact]
    public async Task WithdrawalRejected_NoDeduction()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, CreditUserFromRechargeSms(modemId, 10000));
        await TestHelper.EnqueueAndWaitAsync(channel, CreateWithdrawalRequest(userId, 3000, "Cash out"));

        var wr = await TestHelper.ReadAsync(services, db => db.WithdrawalRequests.FirstAsync(w => w.UserId == userId));
        await TestHelper.EnqueueAndWaitAsync(channel, ProcessWithdrawal(wr.Id, 0, false));

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(10000, user.Balance);
    }

    [Fact]
    public async Task PendingBalanceCheck_Lifecycle()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "Test"));
        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalanceFromSms(modemId, 7500));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        Assert.Equal(7500, sim.Balance);

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(7500, user.Balance);

        var history = await TestHelper.ReadAsync(services, db => db.BalanceHistories.Where(b => b.SimCardId == simId).OrderByDescending(b => b.RecordedAt).FirstAsync());
        Assert.Equal(7500, history.Balance);
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
    public async Task UserBalanceHistory_CreatedOnApproval()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, CreditUserFromRechargeSms(modemId, 5000));

        var history = await TestHelper.ReadAsync(services, db => db.UserBalanceHistories.FirstAsync(h => h.UserId == userId));
        Assert.Equal(5000, history.Amount);
        Assert.Equal(userId, history.UserId);
    }

    [Fact]
    public async Task EuropeanNumberFormat_Recharge_CreditsUser()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, CreditUserFromRechargeSms(modemId, 3788.16m));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(3788.16m, sim.Balance);
        Assert.Equal(3788.16m, user.Balance);
    }

    [Fact]
    public async Task ZeroBalance_FirstCheck_CreditsUser()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        await TestHelper.EnqueueAndWaitAsync(channel, UpdateSimBalance(modemId, 5000, "MeetMob"));

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(5000, sim.Balance);
        Assert.Equal(5000, user.Balance);
    }
}
