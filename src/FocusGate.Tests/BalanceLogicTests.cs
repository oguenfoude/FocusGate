using FocusGate.Core.DTOs;
using FocusGate.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;

namespace FocusGate.Tests;

public class BalanceLogicTests
{
    #region IsMobilisBalanceTrigger

    [Theory]
    [InlineData("Mobilis")]
    [InlineData("77111")]
    [InlineData("610")]
    public void IsMobilisBalanceTrigger_MatchingSender_ReturnsTrue(string sender)
    {
        var msg = new RawSmsMessage
        {
            Sender = sender,
            Content = "Vous avez reçu un montant de 500 DZD DA de 0555123456"
        };
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(msg));
    }

    [Theory]
    [InlineData("  Mobilis")]
    [InlineData("Mobilis ")]
    [InlineData(" Mobilis ")]
    [InlineData(" 77111")]
    [InlineData("610 ")]
    public void IsMobilisBalanceTrigger_WithWhitespace_ReturnsTrue(string sender)
    {
        var msg = new RawSmsMessage
        {
            Sender = sender,
            Content = "Vous avez reçu un montant de 500 DZD DA de 0555123456"
        };
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(msg));
    }

    [Theory]
    [InlineData("Orange")]
    [InlineData("Djezzy")]
    [InlineData("Ooredoo")]
    [InlineData("")]
    [InlineData("6100")]
    public void IsMobilisBalanceTrigger_NonMatchingSender_ReturnsFalse(string sender)
    {
        var msg = new RawSmsMessage
        {
            Sender = sender,
            Content = "Vous avez reçu un montant de 500 DZD DA de 0555123456"
        };
        Assert.False(ModemHandler.IsMobilisBalanceTrigger(msg));
    }

    [Theory]
    [InlineData("Vous avez reçu un montant de 500 DZD DA de 0555123456")]
    [InlineData("Vous avez rechargé 1000 DZD DA au compte 0555123456")]
    public void IsMobilisBalanceTrigger_RechargeContent_ReturnsTrue(string content)
    {
        var msg = new RawSmsMessage { Sender = "Mobilis", Content = content };
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(msg));
    }

    [Theory]
    [InlineData("Solde de votre compte: 5000 DZD")]
    [InlineData("Votre offre a expiré")]
    [InlineData("Info: votre forfait expire bientôt")]
    public void IsMobilisBalanceTrigger_NonRechargeContent_ReturnsFalse(string content)
    {
        var msg = new RawSmsMessage { Sender = "Mobilis", Content = content };
        Assert.False(ModemHandler.IsMobilisBalanceTrigger(msg));
    }

    [Fact]
    public void IsMobilisBalanceTrigger_CaseInsensitive()
    {
        var msg = new RawSmsMessage
        {
            Sender = "Mobilis",
            Content = "Vous avez Reçu un Montant de 500 DZD DA de 0555123456"
        };
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(msg));
    }

    #endregion

    #region IsRechargeSms

    [Theory]
    [InlineData("Vous avez reçu un montant de 500 DZD DA de 0555123456")]
    [InlineData("Montant de 1000 DZD reçu de 0661123456")]
    public void IsRechargeSms_MontantDe_Recu_ReturnsTrue(string content)
    {
        Assert.True(DatabaseWriteChannel.IsRechargeSms(content));
    }

    [Theory]
    [InlineData("Vous avez rechargé 500 DZD DA au compte 0555123456")]
    [InlineData("Solde de votre compte: 5000 DZD")]
    public void IsRechargeSms_NoMontantDe_ReturnsFalse(string content)
    {
        Assert.False(DatabaseWriteChannel.IsRechargeSms(content));
    }

    #endregion

    #region ExtractRechargeAmountFromContent

    [Theory]
    [InlineData("Vous avez reçu un montant de 500 DZD DA de 0555123456", 500)]
    [InlineData("Montant de 1000,50 DZD reçu de 0661123456", 1000.50)]
    [InlineData("reçu un montant de 2500.75 DZD", 2500.75)]
    public void ExtractRechargeAmountFromContent_MontantDe_ReturnsAmount(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value, 2);
    }

    [Theory]
    [InlineData("Vous avez rechargé 1000 DZD DA au 0555123456", 1000)]
    [InlineData("rechargé 300,25 DZD", 300.25)]
    public void ExtractRechargeAmountFromContent_Recharge_ReturnsAmount(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value, 2);
    }

    [Theory]
    [InlineData("Solde de votre compte: 5000 DZD")]
    [InlineData("Votre offre a expiré")]
    [InlineData("")]
    public void ExtractRechargeAmountFromContent_NoAmount_ReturnsNull(string content)
    {
        Assert.Null(DatabaseWriteChannel.ExtractRechargeAmountFromContent(content));
    }

    #endregion

    #region ParseAmount

    [Theory]
    [InlineData("500", 500)]
    [InlineData("1000.50", 1000.50)]
    [InlineData("1,000.50", 1000.50)]
    [InlineData("1.000,50", 1000.50)]
    [InlineData("2500,75", 2500.75)]
    public void ParseAmount_ValidNumbers(string input, decimal expected)
    {
        var result = DatabaseWriteChannel.ParseAmount(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value, 2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("DZD")]
    public void ParseAmount_Invalid_ReturnsNull(string input)
    {
        Assert.Null(DatabaseWriteChannel.ParseAmount(input));
    }

    #endregion

    #region ExtractBalanceFromContent

    [Theory]
    [InlineData("Solde de votre compte: 5000 DZD", 5000)]
    [InlineData("SOLDE: 12500.50 DA", 12500.50)]
    [InlineData("Votre solde est de 350,75 DZD", 350.75)]
    public void ExtractBalanceFromContent_ValidBalance_ReturnsAmount(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value, 2);
    }

    [Theory]
    [InlineData("Pas de solde disponible")]
    [InlineData("Votre offre a expiré")]
    [InlineData("")]
    public void ExtractBalanceFromContent_NoBalance_ReturnsNull(string content)
    {
        Assert.Null(DatabaseWriteChannel.ExtractBalanceFromContent(content));
    }

    #endregion

    #region Pending Balance Check Window

    [Fact]
    public void PendingBalanceCheck_WithinWindow_Claims()
    {
        var channel = CreateDatabaseWriteChannel();
        channel.MarkPendingBalanceCheck(100);
        Assert.True(channel.TryClaimPendingBalanceCheck(100));
    }

    [Fact]
    public void PendingBalanceCheck_AfterWindow_ReturnsFalse()
    {
        var channel = CreateDatabaseWriteChannel();
        channel.MarkPendingBalanceCheck(101);

        // Simulate expired timestamp by setting to 10 minutes ago (beyond the 5-minute window)
        var field = typeof(DatabaseWriteChannel).GetField("_pendingBalanceChecks", BindingFlags.NonPublic | BindingFlags.Instance);
        var dict = (ConcurrentDictionary<long, DateTime>)field!.GetValue(channel)!;
        dict[101] = DateTime.UtcNow.AddMinutes(-10);

        Assert.False(channel.TryClaimPendingBalanceCheck(101));
    }

    [Fact]
    public void PendingBalanceCheck_NotSet_ReturnsFalse()
    {
        var channel = CreateDatabaseWriteChannel();
        Assert.False(channel.TryClaimPendingBalanceCheck(999));
    }

    [Fact]
    public void PendingBalanceCheck_ClaimConsumesEntry()
    {
        var channel = CreateDatabaseWriteChannel();
        channel.MarkPendingBalanceCheck(200);
        Assert.True(channel.TryClaimPendingBalanceCheck(200));
        Assert.False(channel.TryClaimPendingBalanceCheck(200));
    }

    [Fact]
    public void PendingBalanceCheck_ClearRemovesEntry()
    {
        var channel = CreateDatabaseWriteChannel();
        channel.MarkPendingBalanceCheck(300);
        channel.ClearPendingBalanceCheck(300);
        Assert.False(channel.TryClaimPendingBalanceCheck(300));
    }

    #endregion

    #region ConfigMerger Timezone Migration

    [Fact]
    public void ConfigMerger_Timezone0_KeptAsIs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"fg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            var config = new Dictionary<string, string>
            {
                ["modem.timezone_offset_hours"] = "0",
                ["gateway.name"] = "FocusGate",
                ["machine.id"] = "test123"
            };
            var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);

            ConfigMerger.EnsureConfig(configPath);

            var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            Assert.Equal("0", result.RootElement.GetProperty("modem.timezone_offset_hours").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ConfigMerger_AlreadyCorrect_NoMigration()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"fg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            var config = new Dictionary<string, string>
            {
                ["modem.timezone_offset_hours"] = "0",
                ["gateway.name"] = "FocusGate"
            };
            var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);

            ConfigMerger.EnsureConfig(configPath);

            var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            Assert.Equal("0", result.RootElement.GetProperty("modem.timezone_offset_hours").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ConfigMerger_Timezone3_NotOverwritten()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"fg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            var config = new Dictionary<string, string>
            {
                ["modem.timezone_offset_hours"] = "3",
                ["gateway.name"] = "FocusGate"
            };
            var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);

            ConfigMerger.EnsureConfig(configPath);

            var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            Assert.Equal("3", result.RootElement.GetProperty("modem.timezone_offset_hours").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ConfigMerger_CreatesMissingKeys()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"fg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            var config = new Dictionary<string, string>
            {
                ["gateway.name"] = "Test"
            };
            var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);

            ConfigMerger.EnsureConfig(configPath);

            var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            Assert.Equal("0", result.RootElement.GetProperty("modem.timezone_offset_hours").GetString());
            Assert.Equal("Test", result.RootElement.GetProperty("gateway.name").GetString());
            Assert.True(result.RootElement.TryGetProperty("modem.max_count", out _));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    #endregion

    #region Balance End-to-End Scenarios

    [Fact]
    public void E2E_RechargeSms_CreditsCorrectAmount()
    {
        var content = "Vous avez reçu un montant de 500 DZD DA de 0555123456";
        var amount = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(amount);
        Assert.Equal(500, amount.Value, 2);
        Assert.True(DatabaseWriteChannel.IsRechargeSms(content));
    }

    [Fact]
    public void E2E_SoldeSms_ExtractsBalance()
    {
        var content = "Solde de votre compte: 12500 DZD";
        var balance = DatabaseWriteChannel.ExtractBalanceFromContent(content);
        Assert.NotNull(balance);
        Assert.Equal(12500, balance.Value, 2);
    }

    [Fact]
    public void E2E_FrenchNumberFormat_InRecharge()
    {
        var content = "Vous avez reçu un montant de 2.500,75 DZD DA";
        var amount = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(amount);
        Assert.Equal(2500.75m, amount.Value, 2);
    }

    [Fact]
    public void E2E_FrenchNumberFormat_InBalance()
    {
        var content = "Solde de votre compte: 15.000,50 DZD";
        var balance = DatabaseWriteChannel.ExtractBalanceFromContent(content);
        Assert.NotNull(balance);
        Assert.Equal(15000.50m, balance.Value, 2);
    }

    [Fact]
    public void E2E_TrimmedSender_StillMatches()
    {
        var msg = new RawSmsMessage
        {
            Sender = "  Mobilis  ",
            Content = "Vous avez reçu un montant de 1000 DZD DA"
        };
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(msg));
        var amount = DatabaseWriteChannel.ExtractRechargeAmountFromContent(msg.Content);
        Assert.NotNull(amount);
        Assert.Equal(1000, amount.Value, 2);
    }

    [Theory]
    [InlineData("Vous avez reçu un montant de 100 DZD DA de 0555123456", 100)]
    [InlineData("Vous avez reçu un montant de 2000 DZD DA de 0555123456", 2000)]
    [InlineData("Vous avez reçu un montant de 500,50 DZD DA de 0555123456", 500.50)]
    public void E2E_RechargeContent_ExtractsCorrectly(string content, decimal expected)
    {
        Assert.True(DatabaseWriteChannel.IsRechargeSms(content));
        var amount = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(amount);
        Assert.Equal(expected, amount.Value, 2);
    }

    #endregion

    #region ExtractTimestampFromContent

    [Theory]
    [InlineData("Vous avez rechargé 1800.00 DZD DA avec succès le 11/07/2026 23:02:42.", 2026, 7, 11, 22, 2, 42)]
    [InlineData("Vous avez rechargé 1500.00 DZD DA avec succès le 11/07/2026 22:54:59.", 2026, 7, 11, 21, 54, 59)]
    [InlineData("Vous avez rechargé 600.00 DZD DA avec succès le 11/07/2026 22:15:03.", 2026, 7, 11, 21, 15, 3)]
    [InlineData("Vous avez rechargé 100.00 DZD DA avec succès le 11/07/2026 22:09:31.", 2026, 7, 11, 21, 9, 31)]
    [InlineData("Vous avez rechargé 4990.00 DZD DA avec succès le 11/07/2026 21:50:09.", 2026, 7, 11, 20, 50, 9)]
    [InlineData("Vous avez rechargé 1120.00 DZD DA avec succès le 11/07/2026 21:54:55.", 2026, 7, 11, 20, 54, 55)]
    public void ExtractTimestampFromContent_RechargeSms_ReturnsUtcCorrectly(
        string content, int y, int m, int d, int h, int mn, int s)
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(new DateTime(y, m, d, h, mn, s, DateTimeKind.Utc), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Votre offre a expiré")]
    [InlineData("Solde de votre compte: 5000 DZD")]
    [InlineData("Sama, Solde 15.868,06DA")]
    [InlineData("Test message")]
    public void ExtractTimestampFromContent_NoTimestamp_ReturnsNull(string content)
    {
        Assert.Null(HiLinkCommandService.ExtractTimestampFromContent(content));
    }

    [Fact]
    public void ExtractTimestampFromContent_MidnightBoundary_ReturnsCorrectUtc()
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent(
            "Vous avez rechargé 500.00 DZD DA avec succès le 01/01/2027 00:30:00.");
        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc), result);
    }

    [Theory]
    [InlineData("Vous avez rechargé 1000.00 DZD DA avec succès le 23/07/2026 17:18.", 2026, 7, 23, 16, 18, 0)]
    [InlineData("Vous avez rechargé 500.00 DZD DA avec succès le 23/07/2026 16:41.", 2026, 7, 23, 15, 41, 0)]
    public void ExtractTimestampFromContent_TruncatedTimestamp_ReturnsUtcCorrectly(
        string content, int y, int m, int d, int h, int mn, int s)
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(new DateTime(y, m, d, h, mn, s, DateTimeKind.Utc), result);
    }

    #endregion

    #region Helpers

    private static DatabaseWriteChannel CreateDatabaseWriteChannel()
    {
        var logger = new LoggerFactory().CreateLogger<DatabaseWriteChannel>();
        return new DatabaseWriteChannel(new MockServiceProvider(), logger);
    }

    #endregion
}

internal class MockServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
