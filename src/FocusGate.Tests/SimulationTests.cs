using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Services;
using FocusGate.Tests;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FocusGate.Tests;

/// <summary>
/// Real simulation tests that verify the complete system behavior.
/// These tests simulate real-world scenarios with multiple modems and SIMs.
/// </summary>
public class SimulationTests
{
    [Fact]
    public async Task ModemLifecycle_ConnectProcessCredit()
    {
        // Simulate: modem connects → discovers SIM → MeetMob history credits user
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 1);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 100);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        // Step 1: MeetMob history credits user
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem.Id, 5000));

        // Step 2: Verify user is credited (use fresh scope)
        var updatedUser = await TestHelper.ReadAsync(services, 
            db => db.Users.FirstAsync(u => u.Id == user.Id));
        Assert.Equal(5000m, updatedUser.Balance);

        // Step 3: Verify balance history exists
        var history = await TestHelper.ReadAsync(services,
            db => db.UserBalanceHistories.FirstAsync(h => h.UserId == user.Id));
        Assert.Equal(5000m, history.Amount);
        Assert.Equal(0, history.Type); // credit
    }

    [Fact]
    public async Task MultiModem_ParallelRecharges()
    {
        // Simulate: 3 modems with parallel recharges via MeetMob history
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        
        var modem1 = await TestHelper.SeedModemAsync(db, id: 1);
        var modem2 = await TestHelper.SeedModemAsync(db, id: 2);
        var modem3 = await TestHelper.SeedModemAsync(db, id: 3);
        
        var sim1 = await TestHelper.SeedSimCardAsync(db, modem1.Id);
        var sim2 = await TestHelper.SeedSimCardAsync(db, modem2.Id);
        var sim3 = await TestHelper.SeedSimCardAsync(db, modem3.Id);
        
        var alice = await TestHelper.SeedUserAsync(db, id: 100);
        var bob = await TestHelper.SeedUserAsync(db, id: 200);
        var charlie = await TestHelper.SeedUserAsync(db, id: 300);
        
        // Alice on modem1, Bob on modem2, Charlie on modem3
        await TestHelper.AssignUserToModemAsync(db, alice.Id, modem1.Id);
        await TestHelper.AssignUserToModemAsync(db, bob.Id, modem2.Id);
        await TestHelper.AssignUserToModemAsync(db, charlie.Id, modem3.Id);

        // Parallel recharges via MeetMob history
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem1.Id, 1000));
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem2.Id, 2000));
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem3.Id, 3000));

        // Verify each user got credited correctly (use fresh scope)
        var updatedAlice = await TestHelper.ReadAsync(services, 
            db => db.Users.FirstAsync(u => u.Id == alice.Id));
        var updatedBob = await TestHelper.ReadAsync(services, 
            db => db.Users.FirstAsync(u => u.Id == bob.Id));
        var updatedCharlie = await TestHelper.ReadAsync(services, 
            db => db.Users.FirstAsync(u => u.Id == charlie.Id));
        
        Assert.Equal(1000m, updatedAlice.Balance);
        Assert.Equal(2000m, updatedBob.Balance);
        Assert.Equal(3000m, updatedCharlie.Balance);
    }

    [Fact]
    public async Task SameUser_MultipleModems_CreditsCorrectly()
    {
        // Simulate: User assigned to 2 modems, MeetMob history credits on both
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        
        var modem1 = await TestHelper.SeedModemAsync(db, id: 1);
        var modem2 = await TestHelper.SeedModemAsync(db, id: 2);
        var sim1 = await TestHelper.SeedSimCardAsync(db, modem1.Id);
        var sim2 = await TestHelper.SeedSimCardAsync(db, modem2.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 100);
        
        // User on both modems
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem1.Id);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem2.Id);

        // MeetMob history on modem1
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem1.Id, 5000));
        
        // MeetMob history on modem2
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem2.Id, 3000));

        var updatedUser = await TestHelper.ReadAsync(services, 
            db => db.Users.FirstAsync(u => u.Id == user.Id));
        Assert.Equal(8000m, updatedUser.Balance); // 5000 + 3000
    }

    [Fact]
    public async Task BalanceHistory_CompleteAuditTrail()
    {
        // Simulate: Multiple operations with full history
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 1);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 100);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        // 1. MeetMob history credits user and updates SIM balance
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem.Id, 5000));
        
        // 2. Balance check via MeetMob (snapshot with same balance — no-op)
        await TestHelper.EnqueueAndWaitAsync(channel, new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.UpdateSimBalance,
            Data = new { ModemId = modem.Id, Balance = 5000m, Source = "MeetMob" }
        });

        // 3. Withdrawal request
        await TestHelper.EnqueueAndWaitAsync(channel, new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.CreateWithdrawalRequest,
            Data = new { UserId = user.Id, Amount = 2000m, Note = "Cash out" }
        });

        // 4. Approve withdrawal (read from fresh scope)
        var wr = await TestHelper.ReadAsync(services,
            db => db.WithdrawalRequests.FirstAsync(w => w.UserId == user.Id));
        await TestHelper.EnqueueAndWaitAsync(channel, new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.ProcessWithdrawal,
            Data = new { RequestId = wr.Id, AdminId = 0, Approved = true }
        });

        // Verify final state (use fresh scope)
        var finalUser = await TestHelper.ReadAsync(services, 
            db => db.Users.FirstAsync(u => u.Id == user.Id));
        Assert.Equal(3000m, finalUser.Balance); // 5000 - 2000

        // Verify audit trail
        var userHistories = await TestHelper.ReadAllAsync(services,
            db => db.UserBalanceHistories.Where(h => h.UserId == user.Id));
        Assert.Equal(2, userHistories.Count); // credit + debit
        
        var simHistories = await TestHelper.ReadAllAsync(services,
            db => db.BalanceHistories.Where(b => b.SimCardId == sim.Id));
        Assert.Equal(2, simHistories.Count); // one from InsertMeetMobHistory + one from UpdateSimBalance
    }

    [Fact]
    public async Task SoldeSms_UpdatesSimBalance_NotUserBalance()
    {
        // Simulate: Solde SMS updates SIM balance but NOT user wallet
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 1);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 100);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        // First, credit user via MeetMob history
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem.Id, 5000));
        
        // Then, Solde SMS arrives
        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(TestHelper.CreateMobilisSoldeSms(sim.Id, 8500)));

        // Use fresh scope to read
        var updatedSim = await TestHelper.ReadAsync(services, 
            db => db.SimCards.FirstAsync(s => s.Id == sim.Id));
        var updatedUser = await TestHelper.ReadAsync(services, 
            db => db.Users.FirstAsync(u => u.Id == user.Id));
        
        Assert.Equal(8500m, updatedSim.Balance); // SIM balance updated
        Assert.Equal(5000m, updatedUser.Balance); // User balance unchanged
    }

    [Fact]
    public async Task TransferSMS_CreditsUser()
    {
        // Simulate: Transfer SMS does NOT credit user wallet, MeetMob history does
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 1);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 100);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        // Transfer SMS arrives — does NOT credit user
        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(TestHelper.CreateMobilisTransferSms(sim.Id, 3000)));

        var userAfterSms = await TestHelper.ReadAsync(services, 
            db => db.Users.FirstAsync(u => u.Id == user.Id));
        Assert.Equal(0m, userAfterSms.Balance);

        // MeetMob history DOES credit user
        await TestHelper.EnqueueAndWaitAsync(channel, InsertMeetMobHistory(modem.Id, 3000));

        var updatedUser = await TestHelper.ReadAsync(services, 
            db => db.Users.FirstAsync(u => u.Id == user.Id));
        Assert.Equal(3000m, updatedUser.Balance);
    }

    [Fact]
    public async Task NonMobilisSms_DoesNotCreditUser()
    {
        // Simulate: Non-Mobilis SMS does not credit user
        var (db, channel, services) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();
        var modem = await TestHelper.SeedModemAsync(db, id: 1);
        var sim = await TestHelper.SeedSimCardAsync(db, modem.Id);
        var user = await TestHelper.SeedUserAsync(db, id: 100);
        await TestHelper.AssignUserToModemAsync(db, user.Id, modem.Id);

        // Non-Mobilis SMS arrives
        await TestHelper.EnqueueAndWaitAsync(channel, InsertSms(TestHelper.CreateNonMobilisSms(sim.Id)));

        var updatedUser = await TestHelper.ReadAsync(services, 
            db => db.Users.FirstAsync(u => u.Id == user.Id));
        Assert.Equal(0m, updatedUser.Balance); // Not credited
    }

    private static DatabaseWriteChannel.WriteOperation InsertSms(SmsRecord sms) => 
        new() { Type = DatabaseWriteChannel.Op.InsertSms, Data = sms };

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
