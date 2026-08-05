using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class TimeZoneHelperTests
{
    [Fact]
    public void ToDisplayTime_UtcNoon_ReturnsAlgeriaLocalTime()
    {
        var utcNoon = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var result = utcNoon.ToDisplayTime();
        Assert.Equal(new DateTime(2026, 7, 15, 13, 0, 0), result);
    }

    [Fact]
    public void ToDisplayTime_UtcMidnight_ReturnsAlgeria1AM()
    {
        var utcMidnight = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = utcMidnight.ToDisplayTime();
        Assert.Equal(new DateTime(2026, 1, 1, 1, 0, 0), result);
    }

    [Fact]
    public void ToDisplayTime_KindUnspecified_TreatedAsUtc()
    {
        var unspecified = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Unspecified);
        var result = unspecified.ToDisplayTime();
        Assert.Equal(new DateTime(2026, 7, 15, 13, 0, 0), result);
    }

    [Fact]
    public void ToDisplayTime_EndOfDay_CrossesDateBoundary()
    {
        var utc23h = new DateTime(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc);
        var result = utc23h.ToDisplayTime();
        Assert.Equal(new DateTime(2027, 1, 1, 0, 30, 0), result);
    }

    [Fact]
    public void ToDisplayTime_AlwaysAddsOneHourForAlgeria()
    {
        var utc = new DateTime(2026, 6, 15, 8, 45, 0, DateTimeKind.Utc);
        var result = utc.ToDisplayTime();
        Assert.Equal(9, result.Hour);
        Assert.Equal(45, result.Minute);
    }
}
