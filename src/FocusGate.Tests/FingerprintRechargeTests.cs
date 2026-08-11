using FocusGate.Core.Enums;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Data;
using FocusGate.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FocusGate.Tests;

public class FingerprintRechargeTests
{
    [Fact]
    public void ExtractRechargeFingerprint_FrenchTransactionId_ReturnsTxFingerprint()
    {
        var content = "?Vous avez reçu un montant de 2000.00 DZD,numéro de la transaction est 042391000";
        var fp = DatabaseWriteChannel.ExtractRechargeFingerprint(content, 1, 2000m, DateTime.UtcNow);
        Assert.Equal("TX:042391000", fp);
    }

    [Fact]
    public void ExtractRechargeFingerprint_ArabicTransactionId_ReturnsTxFingerprint()
    {
        var content = "لقد استلمتم مبلغا قدره 1000.00 دج رقم المعاملة 042399555";
        var fp = DatabaseWriteChannel.ExtractRechargeFingerprint(content, 1, 1000m, DateTime.UtcNow);
        Assert.Equal("TX:042399555", fp);
    }

    [Fact]
    public void ExtractRechargeFingerprint_FrenchCarrierTimestamp_ReturnsTsFingerprint()
    {
        var content = "Vous avez rechargé 1000.00 DZD DA avec succès le 10/08/2026 18:48:09. Profitez d";
        var fp = DatabaseWriteChannel.ExtractRechargeFingerprint(content, 1, 1000m, DateTime.UtcNow);
        Assert.Equal("TS:10/08/2026 18:48:09", fp);
    }

    [Fact]
    public void ExtractRechargeFingerprint_ArabicCarrierTimestamp_ReturnsTsFingerprint()
    {
        var content = "لقد تم تعبئة رصيدكم بمبلغ 500.00 دج بتاريخ 10/08/2026 22:30:15";
        var fp = DatabaseWriteChannel.ExtractRechargeFingerprint(content, 1, 500m, DateTime.UtcNow);
        Assert.Equal("TS:10/08/2026 22:30:15", fp);
    }

    [Fact]
    public void ExtractRechargeFingerprint_NoIdOrDate_ReturnsFallbackFingerprint()
    {
        var content = "Vous avez rechargé 500.00 DZD";
        var date = new DateTime(2026, 8, 10, 20, 0, 0, DateTimeKind.Utc);
        var fp = DatabaseWriteChannel.ExtractRechargeFingerprint(content, 7, 500m, date);
        Assert.Equal("FALLBACK:Sim=7_Amt=500.00_20260810200000", fp);
    }

    [Fact]
    public async Task TenConsecutiveFastRechargesInSameMinute_AllTenCreditedAccurately()
    {
        var (db, channel, provider) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();

        var modem = await TestHelper.SeedModemAsync(db, id: 101);
        var sim = await TestHelper.SeedSimCardAsync(db, modemId: 101, id: 201);
        var user = await TestHelper.SeedUserAsync(db, id: 301, balance: 0m);
        await TestHelper.SeedUserModemAsync(db, userId: 301, modemId: 101);

        // Send 10 consecutive recharges of 100 DZD with distinct transaction IDs in 10 seconds
        for (int i = 1; i <= 10; i++)
        {
            var content = $"?Vous avez reçu un montant de 100.00 DZD,numéro de la transaction est 04239100{i}";
            channel.EnqueueInsertSms(201, "Mobilis", content, SmsType.Transfer, DateTime.UtcNow);
        }

        // Wait for queue processing
        await Task.Delay(500);

        var updatedUser = await db.Users.FindAsync(301L);
        Assert.NotNull(updatedUser);
        // All 10 recharges must be credited: 10 x 100 DZD = 1,000 DZD
        Assert.Equal(1000.00m, updatedUser.Balance);

        var histories = await db.UserBalanceHistories.Where(h => h.UserId == 301L).ToListAsync();
        Assert.Equal(10, histories.Count);

        // Verify each transaction ID is recorded in the history notes
        for (int i = 1; i <= 10; i++)
        {
            Assert.Contains(histories, h => h.Note != null && h.Note.Contains($"TX:04239100{i}"));
        }
    }

    [Fact]
    public async Task CompanionTransferAndNotificationPair_CreditedExactlyOnce()
    {
        var (db, channel, provider) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();

        var modem = await TestHelper.SeedModemAsync(db, id: 102);
        var sim = await TestHelper.SeedSimCardAsync(db, modemId: 102, id: 202);
        var user = await TestHelper.SeedUserAsync(db, id: 302, balance: 0m);
        await TestHelper.SeedUserModemAsync(db, userId: 302, modemId: 102);

        // SMS 1: Transfer receipt with Transaction ID
        var sms1 = "?Vous avez reçu un montant de 2000.00 DZD,numéro de la transaction est 042399888";
        channel.EnqueueInsertSms(202, "Mobilis", sms1, SmsType.Transfer, DateTime.UtcNow);

        // SMS 2: Companion confirmation notification 1 second later
        var sms2 = "Vous avez rechargé 2000.00 DZD DA avec succès le 10/08/2026 22:45:10. Profitez d";
        channel.EnqueueInsertSms(202, "Mobilis", sms2, SmsType.Recharge, DateTime.UtcNow.AddSeconds(1));

        await Task.Delay(400);

        var updatedUser = await db.Users.FindAsync(302L);
        Assert.NotNull(updatedUser);
        // Credited EXACTLY once: 2,000 DZD (not 4,000 DZD)
        Assert.Equal(2000.00m, updatedUser.Balance);

        var histories = await db.UserBalanceHistories.Where(h => h.UserId == 302L).ToListAsync();
        Assert.Single(histories);
        Assert.Contains("TX:042399888", histories[0].Note);
    }

    [Fact]
    public async Task ScratchCardRecharge_DeduplicatedEvenAfterHoursOrRestart()
    {
        var (db, channel, provider) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();

        var modem = await TestHelper.SeedModemAsync(db, id: 103);
        var sim = await TestHelper.SeedSimCardAsync(db, modemId: 103, id: 203);
        var user = await TestHelper.SeedUserAsync(db, id: 303, balance: 500m);
        await TestHelper.SeedUserModemAsync(db, userId: 303, modemId: 103);

        // First arrival of scratch card SMS
        var sms = "Vous avez rechargé 1000.00 DZD DA avec succès le 10/08/2026 18:48:09. Profitez d";
        channel.EnqueueInsertSms(203, "Mobilis", sms, SmsType.Recharge, DateTime.UtcNow);

        await Task.Delay(300);

        var userAfterFirst = await db.Users.FindAsync(303L);
        Assert.Equal(1500.00m, userAfterFirst!.Balance);

        // Re-deliver exact same SMS (simulating modem re-reading inbox after reboot 4 hours later)
        channel.EnqueueInsertSms(203, "Mobilis", sms, SmsType.Recharge, DateTime.UtcNow.AddHours(4));

        await Task.Delay(300);

        var userAfterReboot = await db.Users.FindAsync(303L);
        // Must stay 1,500 DZD (0 duplicate credit)
        Assert.Equal(1500.00m, userAfterReboot!.Balance);

        var histories = await db.UserBalanceHistories.Where(h => h.UserId == 303L).ToListAsync();
        Assert.Single(histories);
    }

    [Fact]
    public async Task ThirtyRechargesDifferentAmountsInSameSecond_AllThirtyCreditedAccurately()
    {
        var (db, channel, provider) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();

        var modem = await TestHelper.SeedModemAsync(db, id: 104);
        var sim = await TestHelper.SeedSimCardAsync(db, modemId: 104, id: 204);
        var user = await TestHelper.SeedUserAsync(db, id: 304, balance: 100m);
        await TestHelper.SeedUserModemAsync(db, userId: 304, modemId: 104);

        decimal expectedTotalRecharge = 0m;
        var now = DateTime.UtcNow;

        // 30 different amounts: 50, 100, 150, 200, ... 1500
        for (int i = 1; i <= 30; i++)
        {
            decimal amt = i * 50m;
            expectedTotalRecharge += amt;

            var content = $"?Vous avez reçu un montant de {amt:F2} DZD,numéro de la transaction est 0423980{i:D2}";
            channel.EnqueueInsertSms(204, "Mobilis", content, SmsType.Transfer, now);
        }

        // Wait for single-queue channel processing
        await Task.Delay(1000);

        var updatedUser = await db.Users.FindAsync(304L);
        Assert.NotNull(updatedUser);

        // Expected: initial 100 + sum(50..1500 = 23250) = 23350 DZD
        Assert.Equal(100m + expectedTotalRecharge, updatedUser.Balance);

        var histories = await db.UserBalanceHistories.Where(h => h.UserId == 304L).ToListAsync();
        Assert.Equal(30, histories.Count);

        for (int i = 1; i <= 30; i++)
        {
            decimal amt = i * 50m;
            Assert.Contains(histories, h => h.Amount == amt && h.Note != null && h.Note.Contains($"TX:0423980{i:D2}"));
        }
    [Fact]
    public async Task ReverseArrivalOrder_ConfirmationSmsFirstThenTransferSms_CreditedExactlyOnce()
    {
        var (db, channel, provider) = await TestHelper.CreateInMemoryDatabaseWithChannelAsync();

        var modem = await TestHelper.SeedModemAsync(db, id: 105);
        var sim = await TestHelper.SeedSimCardAsync(db, modemId: 105, id: 205);
        var user = await TestHelper.SeedUserAsync(db, id: 305, balance: 0m);
        await TestHelper.SeedUserModemAsync(db, userId: 305, modemId: 105);

        // SMS 1: Confirmation notification arrives FIRST
        var sms1 = "Vous avez rechargé 700.00 DZD DA avec succès le 11/08/2026 16:30:34. Profitez d";
        channel.EnqueueInsertSms(205, "Mobilis", sms1, SmsType.Recharge, DateTime.UtcNow);

        // SMS 2: Transfer receipt arrives 1 second SECOND
        var sms2 = "?Vous avez reçu un montant de 700.00 DZD,numéro de la transaction est 0424010000";
        channel.EnqueueInsertSms(205, "Mobilis", sms2, SmsType.Transfer, DateTime.UtcNow.AddSeconds(1));

        await Task.Delay(400);

        var updatedUser = await db.Users.FindAsync(305L);
        Assert.NotNull(updatedUser);
        // Credited EXACTLY once: 700 DZD (not 1400 DZD)
        Assert.Equal(700.00m, updatedUser.Balance);

        var histories = await db.UserBalanceHistories.Where(h => h.UserId == 305L).ToListAsync();
        Assert.Single(histories);
    }
}

