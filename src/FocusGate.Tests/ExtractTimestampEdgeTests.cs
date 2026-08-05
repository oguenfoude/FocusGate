using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class ExtractTimestampEdgeTests
{
    [Theory]
    [InlineData("le 01/01/2027 00:30:00", 2026, 12, 31, 23, 30, 0)]
    [InlineData("le 01/07/2026 00:00:00", 2026, 6, 30, 23, 0, 0)]
    public void ExtractTimestamp_MidnightBoundary_ConvertsCorrectly(
        string content, int y, int m, int d, int h, int mn, int s)
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(new DateTime(y, m, d, h, mn, s, DateTimeKind.Utc), result);
    }

    [Theory]
    [InlineData("le 31/12/2026 23:59:59", 2026, 12, 31, 22, 59, 59)]
    [InlineData("le 31/12/2026 12:00:00", 2026, 12, 31, 11, 0, 0)]
    public void ExtractTimestamp_EndOfYear_ConvertsCorrectly(
        string content, int y, int m, int d, int h, int mn, int s)
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(new DateTime(y, m, d, h, mn, s, DateTimeKind.Utc), result);
    }

    [Theory]
    [InlineData("le 01/01/2026 01:00:00", 2026, 1, 1, 0, 0, 0)]
    [InlineData("le 01/01/2026 02:00:00", 2026, 1, 1, 1, 0, 0)]
    public void ExtractTimestamp_StartOfYear_UTCZero_ConvertsCorrectly(
        string content, int y, int m, int d, int h, int mn, int s)
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(new DateTime(y, m, d, h, mn, s, DateTimeKind.Utc), result);
    }

    [Theory]
    [InlineData("le 15/06/2026 14:30:15", 2026, 6, 15, 13, 30, 15)]
    public void ExtractTimestamp_RegularTime_ReturnsCorrectUTC(
        string content, int y, int m, int d, int h, int mn, int s)
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(new DateTime(y, m, d, h, mn, s, DateTimeKind.Utc), result);
    }

    [Theory]
    [InlineData("le 23/07/2026 17:18", 2026, 7, 23, 16, 18, 0)]
    [InlineData("le 23/07/2026 08:05", 2026, 7, 23, 7, 5, 0)]
    public void ExtractTimestamp_NoSeconds_DefaultsToZero(
        string content, int y, int m, int d, int h, int mn, int s)
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(new DateTime(y, m, d, h, mn, s, DateTimeKind.Utc), result);
    }

    [Theory]
    [InlineData("Vous avez rechargé le 01/01/2027 00:30:00.")]
    [InlineData("Succès le 15/06/2026 14:30:15")]
    public void ExtractTimestamp_SurroundingText_FindsDate(string content)
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent(content);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No date here")]
    [InlineData("Solde de votre compte: 5000 DZD")]
    [InlineData("Sama, Solde 15.868,06DA")]
    public void ExtractTimestamp_NoDate_ReturnsNull(string content)
    {
        Assert.Null(HiLinkCommandService.ExtractTimestampFromContent(content));
    }

    [Fact]
    public void ExtractTimestamp_InvalidDate_ReturnsNull()
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent("le 32/13/2026 12:00:00");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractTimestamp_InvalidTime_ReturnsNull()
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent("le 15/06/2026 25:00:00");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractTimestamp_MultipleDates_PicksFirst()
    {
        var result = HiLinkCommandService.ExtractTimestampFromContent(
            "le 15/06/2026 14:30:00 et le 20/07/2026 10:00:00");
        Assert.NotNull(result);
        Assert.Equal(2026, result!.Value.Year);
        Assert.Equal(6, result.Value.Month);
        Assert.Equal(15, result.Value.Day);
    }

    [Fact]
    public void ExtractTimestamp_VeryLongContent_FindsDate()
    {
        var prefix = new string('x', 5000);
        var content = prefix + " le 15/06/2026 14:30:00";
        var result = HiLinkCommandService.ExtractTimestampFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 6, 15, 13, 30, 0, DateTimeKind.Utc), result);
    }
}
