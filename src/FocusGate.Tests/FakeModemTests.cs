using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Services;
using FocusGate.Tests.Mocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FocusGate.Tests;

public class FakeModemTests
{
    [Fact]
    public async Task FakeRecharge_CreditsUser()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 1);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 100);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        await TestHelper.EnqueueAndWaitAsync(channel, new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.InsertMeetMobHistory,
            Data = new
            {
                ModemId = modem.Id,
                Records = new List<MeetMobRechargeRecordDto>
                {
                    new() { TradeTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Amount = "5000.00" }
                }
            }
        });

        var updatedUser = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == user.Id));
        Assert.Equal(5000m, updatedUser.Balance);
    }

    [Fact]
    public async Task FakeTransfer_CreditsUser()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 2);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 200);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        await TestHelper.EnqueueAndWaitAsync(channel, new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.InsertMeetMobHistory,
            Data = new
            {
                ModemId = modem.Id,
                Records = new List<MeetMobRechargeRecordDto>
                {
                    new() { TradeTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Amount = "3000.00" }
                }
            }
        });

        var updatedUser = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == user.Id));
        Assert.Equal(3000m, updatedUser.Balance);
    }

    [Fact]
    public async Task FakeSolde_UpdatesSimBalance()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 3);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);

        var sms = TestHelper.CreateMobilisSoldeSms(sim.Id, 8500);
        await TestHelper.EnqueueAndWaitAsync(channel, new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.InsertSms,
            Data = sms
        });

        var updatedSim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == sim.Id));
        Assert.Equal(8500m, updatedSim.Balance);
    }

    [Fact]
    public async Task FakeBalanceHistory_IsRecorded()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 4);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);

        await TestHelper.EnqueueAndWaitAsync(channel, new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.UpdateSimBalance,
            Data = new { ModemId = modem.Id, Balance = 5000m, Source = "MeetMob" }
        });

        var history = await TestHelper.ReadAsync(services, db => db.BalanceHistories.FirstAsync(b => b.SimCardId == sim.Id));
        Assert.Equal(5000m, history.Balance);
        Assert.Equal(BalanceSource.MeetMob, history.Source);
    }

    [Fact]
    public async Task MultipleSimCards_IndependentBalances()
    {
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 5);
        var sim1 = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var sim2 = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 500);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        // Credit via MeetMob history (two increasing balances)
        await TestHelper.EnqueueAndWaitAsync(channel, new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.InsertMeetMobHistory,
            Data = new
            {
                ModemId = modem.Id,
                Records = new List<MeetMobRechargeRecordDto>
                {
                    new() { TradeTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Amount = "1000.00" },
                    new() { TradeTime = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd HH:mm:ss"), Amount = "3000.00" }
                }
            }
        });

        var updatedUser = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == user.Id));
        // Each record is an independent recharge: 1000 + 3000 = 4000
        Assert.Equal(4000m, updatedUser.Balance);
    }

    [Fact]
    public async Task MockAtCommandService_SimulatesModem()
    {
        var mock = new MockHiLinkCommandService();
        await mock.OpenAsync("MOCK1");
        Assert.True(mock.IsOpen);

        var imei = await mock.GetImeiAsync();
        Assert.Equal("MOCKIMEI12345678", imei);

        var balance = await mock.GetBalanceAsync();
        Assert.Equal(1500.00m, balance);

        var sms = await mock.ReadAllSmsAsync();
        Assert.Empty(sms);

        var ussd = await mock.SendUssdAsync("*222#");
        Assert.Contains("1500", ussd);

        mock.Close();
        Assert.False(mock.IsOpen);
    }

    [Fact]
    public async Task MockMeetMob_SimulatesLogin()
    {
        var mock = new MockMeetMobService();
        var token = await mock.LoginAsync("603019123", "0555123456");
        Assert.NotNull(token);
        Assert.Equal("mock-token", token);

        mock.SetBalance("0555123456", 3000m);
        var balance = await mock.GetBalanceAsync("0555123456", token!);
        Assert.Equal(3000m, balance);
    }
}
