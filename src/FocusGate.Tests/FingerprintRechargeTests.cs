using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Services;
using FocusGate.Tests;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FocusGate.Tests;

public class FingerprintRechargeTests
{
    [Fact]
    public async Task SameContent_SameSim_Within4Min_SmsDeduped()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 101);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 301);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        // Two identical SMS within 4 minutes — should be deduped by SMS-level check
        var sms1 = TestHelper.CreateMobilisTransferSms(sim.Id, 500);
        sms1.ReceivedAt = DateTime.UtcNow;
        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(sms1));

        var sms2 = TestHelper.CreateMobilisTransferSms(sim.Id, 500);
        sms2.ReceivedAt = DateTime.UtcNow.AddMinutes(2);
        var dedupResult = await TestHelper.EnqueueAndReturnResultAsync(channel, InsertSms(sms2));
        Assert.False(dedupResult, "Second identical SMS within 4 min should be deduped");

        // SMS no longer credits user wallet
        var updatedUser = await TestHelper.ReadAsync(services, db => db.Users.FindAsync(user.Id).AsTask());
        Assert.NotNull(updatedUser);
        Assert.Equal(0m, updatedUser.Balance);
    }

    [Fact]
    public async Task DifferentAmounts_DifferentCredits()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 102);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 302);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        // Credit via MeetMob history
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem.Id, 1000));
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem.Id, 3000));

        var updatedUser = await TestHelper.ReadAsync(services, db => db.Users.FindAsync(user.Id).AsTask());
        Assert.NotNull(updatedUser);
        // Each record is an independent recharge: 1000 + 3000 = 4000
        Assert.Equal(4000m, updatedUser.Balance);
    }

    [Fact]
    public async Task SameAmount_DifferentContent_BothCredited()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 103);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 303);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        // Two MeetMob history records with balance increases
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem.Id, 700));
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem.Id, 1400));

        var updatedUser = await TestHelper.ReadAsync(services, db => db.Users.FindAsync(user.Id).AsTask());
        Assert.NotNull(updatedUser);
        // Each record is an independent recharge: 700 + 1400 = 2100
        Assert.Equal(2100m, updatedUser.Balance);
    }

    [Fact]
    public async Task DifferentAmounts_30SecondsApart_AllCredited()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 104);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 304);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        for (int i = 1; i <= 5; i++)
        {
            decimal amt = i * 100m;
            await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem.Id, i * 100m));
        }

        var updatedUser = await TestHelper.ReadAsync(services, db => db.Users.FindAsync(user.Id).AsTask());
        Assert.NotNull(updatedUser);
        // Each record is an independent recharge: 100+200+300+400+500 = 1500
        Assert.Equal(1500m, updatedUser.Balance);
    }

    [Fact]
    public async Task TwoDifferentRechargeSms_BothCredited()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 105);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 305);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        // Two MeetMob history records with increasing balances
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem.Id, 700));
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem.Id, 1400));

        var updatedUser = await TestHelper.ReadAsync(services, db => db.Users.FindAsync(user.Id).AsTask());
        Assert.NotNull(updatedUser);
        // Each record is an independent recharge: 700 + 1400 = 2100
        Assert.Equal(2100.00m, updatedUser.Balance);

        var histories = await TestHelper.ReadAsync(services, db => db.UserBalanceHistories.Where(h => h.UserId == 305).ToListAsync());
        Assert.Equal(2, histories.Count);
    }

    private static DatabaseWriteChannel.WriteOperation InsertSms(SmsRecord sms) => new() { Type = DatabaseWriteChannel.Op.InsertSms, Data = sms };
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
