using FocusGate.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;

namespace FocusGate.Tests;

public class PendingBalanceCheckTests
{
    [Fact]
    public void PendingBalanceCheck_MultipleModems_Independent()
    {
        var channel = CreateChannel();
        channel.MarkPendingBalanceCheck(1);
        channel.MarkPendingBalanceCheck(2);
        channel.MarkPendingBalanceCheck(3);

        Assert.True(channel.TryClaimPendingBalanceCheck(1, out _));
        Assert.True(channel.TryClaimPendingBalanceCheck(2, out _));
        Assert.True(channel.TryClaimPendingBalanceCheck(3, out _));
    }

    [Fact]
    public void PendingBalanceCheck_ClaimOne_DoesNotAffectOthers()
    {
        var channel = CreateChannel();
        channel.MarkPendingBalanceCheck(10);
        channel.MarkPendingBalanceCheck(20);

        Assert.True(channel.TryClaimPendingBalanceCheck(10, out _));
        Assert.False(channel.TryClaimPendingBalanceCheck(10, out _));
        Assert.True(channel.TryClaimPendingBalanceCheck(20, out _));
    }

    [Fact]
    public void PendingBalanceCheck_MarkTwice_OverwritesTimestamp()
    {
        var channel = CreateChannel();
        channel.MarkPendingBalanceCheck(50);
        channel.MarkPendingBalanceCheck(50);

        Assert.True(channel.TryClaimPendingBalanceCheck(50, out _));
    }

    [Fact]
    public void PendingBalanceCheck_ClearNonExistent_NoError()
    {
        var channel = CreateChannel();
        channel.ClearPendingBalanceCheck(999);
        Assert.False(channel.TryClaimPendingBalanceCheck(999, out _));
    }

    [Fact]
    public void PendingBalanceCheck_ExactlyAt10Minutes_Claims()
    {
        var channel = CreateChannel();
        channel.MarkPendingBalanceCheck(70);

        var field = typeof(DatabaseWriteChannel).GetField("_pendingBalanceChecks", BindingFlags.NonPublic | BindingFlags.Instance);
        var dict = (ConcurrentDictionary<long, (DateTime At, decimal? RechargeAmount)>)field!.GetValue(channel)!;
        dict[70] = (DateTime.UtcNow.AddMinutes(-9.9), null);

        Assert.True(channel.TryClaimPendingBalanceCheck(70, out _));
    }

    [Fact]
    public void PendingBalanceCheck_At10Minutes1Second_ReturnsFalse()
    {
        var channel = CreateChannel();
        channel.MarkPendingBalanceCheck(71);

        var field = typeof(DatabaseWriteChannel).GetField("_pendingBalanceChecks", BindingFlags.NonPublic | BindingFlags.Instance);
        var dict = (ConcurrentDictionary<long, (DateTime At, decimal? RechargeAmount)>)field!.GetValue(channel)!;
        dict[71] = (DateTime.UtcNow.AddMinutes(-10.1), null);

        Assert.False(channel.TryClaimPendingBalanceCheck(71, out _));
    }

    [Fact]
    public void PendingBalanceCheck_ZeroModemId_Works()
    {
        var channel = CreateChannel();
        channel.MarkPendingBalanceCheck(0);
        Assert.True(channel.TryClaimPendingBalanceCheck(0, out _));
    }

    [Fact]
    public void PendingBalanceCheck_NegativeModemId_Works()
    {
        var channel = CreateChannel();
        channel.MarkPendingBalanceCheck(-1);
        Assert.True(channel.TryClaimPendingBalanceCheck(-1, out _));
    }

    private static DatabaseWriteChannel CreateChannel()
    {
        var logger = new LoggerFactory().CreateLogger<DatabaseWriteChannel>();
        return new DatabaseWriteChannel(new MockServiceProvider(), logger);
    }
}
