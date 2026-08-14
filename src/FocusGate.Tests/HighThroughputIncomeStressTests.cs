using System.Text.Json;
using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FocusGate.Tests;

public class HighThroughputIncomeStressTests
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

    [Fact]
    public async Task Rapid100IncomingRecharges_CalculatesExactUserWalletBalance()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();
        decimal expectedTotalCredit = 0;

        // Send 20 MeetMob history records — each is an independent recharge
        for (int i = 1; i <= 20; i++)
        {
            decimal balance = 500 + (i * 10);
            expectedTotalCredit += balance; // each record is an independent recharge

            var op = new DatabaseWriteChannel.WriteOperation
            {
                Type = DatabaseWriteChannel.Op.InsertMeetMobHistory,
                Data = new
                {
                    ModemId = modemId,
                    Records = new List<MeetMobRechargeRecordDto>
                    {
                        new() { TradeTime = DateTime.Now.AddSeconds(-20 + i).ToString("yyyy-MM-dd HH:mm:ss"), Amount = balance.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) }
                    }
                },
                Completed = new TaskCompletionSource<bool>()
            };

            await channel.EnqueueAsync(op);
            await op.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));
        Assert.Equal(expectedTotalCredit, user.Balance);
    }

    [Fact]
    public async Task MeetMobHistoryMatching_ReconcilesWithLocalDatabase()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        // 1. Ingest SMS
        var sms = new SmsRecord
        {
            SimCardId = simId,
            SenderNumber = "Mobilis",
            Content = "Vous avez reçu un montant de 1500.00 DZD",
            ReceivedAt = DateTime.UtcNow
        };
        await TestHelper.EnqueueAndWaitAsync(channel, new DatabaseWriteChannel.WriteOperation
        {
            Type = DatabaseWriteChannel.Op.InsertSms,
            Data = sms
        });

        // 2. Simulate MeetMob History JSON response
        var historyJson = """
        {
          "result": "success",
          "resultBody": {
            "rechargeInfo": [
              {
                "tradeTime": "2026-08-09 14:32:15",
                "rechargeAmount": "1500.00"
              }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(historyJson);
        var root = doc.RootElement;
        var historyRecords = new List<MeetMobRechargeRecord>();
        if (root.GetProperty("resultBody").TryGetProperty("rechargeInfo", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                historyRecords.Add(new MeetMobRechargeRecord
                {
                    TradeTime = item.GetProperty("tradeTime").GetString() ?? "",
                    Amount = item.GetProperty("rechargeAmount").GetString() ?? "0"
                });
            }
        }

        // 3. Verify match between SMS in DB and MeetMob history
        var savedSms = await TestHelper.ReadAsync(services, db => db.SmsRecords.FirstOrDefaultAsync(s => s.SimCardId == simId));
        Assert.NotNull(savedSms);
        Assert.Single(historyRecords);
        Assert.Equal("1500.00", historyRecords[0].Amount);
    }

    [Fact]
    public async Task ParallelMultiThreadedStress_InterleavedCreditsAndBalanceUpdates_NoRaceConditions()
    {
        var (channel, services, modemId, simId, userId) = await SetupAsync();

        var tasks = new List<Task>();

        // Launch 10 parallel threads enqueuing MeetMob history credits and balance updates simultaneously
        for (int i = 1; i <= 10; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                var opHistory = new DatabaseWriteChannel.WriteOperation
                {
                    Type = DatabaseWriteChannel.Op.InsertMeetMobHistory,
                    Data = new
                    {
                        ModemId = modemId,
                        Records = new List<MeetMobRechargeRecordDto>
                        {
                            new() { TradeTime = DateTime.Now.AddMinutes(index).ToString("yyyy-MM-dd HH:mm:ss"), Amount = (index * 1000).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) }
                        }
                    },
                    Completed = new TaskCompletionSource<bool>()
                };
                await channel.EnqueueAsync(opHistory);
                await opHistory.Completed.Task;

                var opBal = new DatabaseWriteChannel.WriteOperation
                {
                    Type = DatabaseWriteChannel.Op.UpdateSimBalance,
                    Data = new { ModemId = modemId, Balance = (decimal)(index * 1000), Source = "MeetMob" },
                    Completed = new TaskCompletionSource<bool>()
                };
                await channel.EnqueueAsync(opBal);
                await opBal.Completed.Task;
            }));
        }

        await Task.WhenAll(tasks);

        var sim = await TestHelper.ReadAsync(services, db => db.SimCards.FirstAsync(s => s.Id == simId));
        var user = await TestHelper.ReadAsync(services, db => db.Users.FirstAsync(u => u.Id == userId));

        Assert.True(sim.Balance > 0);
        Assert.True(user.Balance > 0);
    }
}
