using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class ParseAmountComprehensiveTests
{
    [Theory]
    [InlineData("100", 100)]
    [InlineData("1000", 1000)]
    [InlineData("999999", 999999)]
    public void ParseAmount_PlainInteger_ReturnsDecimal(string input, decimal expected)
    {
        var result = DatabaseWriteChannel.ParseAmount(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("100.50", 100.50)]
    [InlineData("99.99", 99.99)]
    [InlineData("0.01", 0.01)]
    public void ParseAmount_DecimalWithDot_ReturnsDecimal(string input, decimal expected)
    {
        var result = DatabaseWriteChannel.ParseAmount(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("100,50", 100.50)]
    [InlineData("99,99", 99.99)]
    [InlineData("0,01", 0.01)]
    public void ParseAmount_DecimalWithComma_ReturnsDecimal(string input, decimal expected)
    {
        var result = DatabaseWriteChannel.ParseAmount(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1.000", 1000)]
    [InlineData("12.345", 12345)]
    [InlineData("999.999", 999999)]
    public void ParseAmount_ThreeDigitsAfterDot_ThousandsSeparator(string input, decimal expected)
    {
        var result = DatabaseWriteChannel.ParseAmount(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1.000,50", 1000.50)]
    [InlineData("12.345,67", 12345.67)]
    public void ParseAmount_EuropeanFormat_DotThousandsCommaDecimal(string input, decimal expected)
    {
        var result = DatabaseWriteChannel.ParseAmount(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1,000.50", 1000.50)]
    [InlineData("12,345.67", 12345.67)]
    public void ParseAmount_USFormat_CommaThousandsDotDecimal(string input, decimal expected)
    {
        var result = DatabaseWriteChannel.ParseAmount(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("DZD")]
    [InlineData("DA")]
    [InlineData("DZD 100")]
    [InlineData("hello world")]
    public void ParseAmount_Invalid_ReturnsNull(string input)
    {
        var result = DatabaseWriteChannel.ParseAmount(input);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0,0", 0)]
    [InlineData("0.0", 0)]
    public void ParseAmount_Zero_ReturnsZero(string input, decimal expected)
    {
        var result = DatabaseWriteChannel.ParseAmount(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("100,000", 100.000)]
    [InlineData("999,999", 999.999)]
    public void ParseAmount_CommaOnly_TreatedAsDecimal(string input, decimal expected)
    {
        var result = DatabaseWriteChannel.ParseAmount(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseAmount_LargeNumber_NoOverflow()
    {
        var result = DatabaseWriteChannel.ParseAmount("999999999,99");
        Assert.NotNull(result);
        Assert.Equal(999999999.99m, result);
    }

    [Theory]
    [InlineData("0,5", 0.5)]
    [InlineData("5,5", 5.5)]
    [InlineData("9,9", 9.9)]
    public void ParseAmount_SingleDigitAfterComma(string input, decimal expected)
    {
        var result = DatabaseWriteChannel.ParseAmount(input);
        Assert.Equal(expected, result);
    }
}
