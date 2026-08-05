using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class ExtractBalanceFromContentTests
{
    [Theory]
    [InlineData("Solde de votre compte: 5000 DZD", 5000)]
    [InlineData("Votre solde est 12500 DA", 12500)]
    [InlineData("SOLDE: 350 DZD", 350)]
    public void ExtractBalanceFromContent_PlainInteger_ReturnsDecimal(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent(content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Solde: 350,75 DZD", 350.75)]
    [InlineData("Votre solde est 99,99 DA", 99.99)]
    public void ExtractBalanceFromContent_CommaDecimal_ReturnsDecimal(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent(content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Solde: 100.50 DZD", 100.50)]
    [InlineData("Votre solde est 99.99 DA", 99.99)]
    public void ExtractBalanceFromContent_DotDecimal_ReturnsDecimal(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent(content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Solde: 1.234,56 DA", 1234.56)]
    [InlineData("Votre solde est 12.345,67 DZD", 12345.67)]
    public void ExtractBalanceFromContent_EuropeanFormat_ReturnsDecimal(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent(content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Solde: 1,234.56 DA", 1234.56)]
    public void ExtractBalanceFromContent_USFormat_ReturnsDecimal(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent(content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Bonjour, votre compte est actif")]
    [InlineData("Vous avez reçu un montant de 500 DZD")]
    [InlineData("Rechargez votre compte maintenant")]
    [InlineData("")]
    [InlineData("Hello world")]
    public void ExtractBalanceFromContent_NoSolde_ReturnsNull(string content)
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent(content);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("Solde:")]
    [InlineData("Solde est vide")]
    [InlineData("Solde")]
    public void ExtractBalanceFromContent_SoldeButNoNumber_ReturnsNull(string content)
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent(content);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractBalanceFromContent_CaseInsensitive()
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent("solde: 5000 DZD");
        Assert.Equal(5000, result);
    }

    [Fact]
    public void ExtractBalanceFromContent_SoldeWithMultipleNumbers_TakesFirst()
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent("Solde: 5000 DZD, expiration: 30 jours");
        Assert.Equal(5000, result);
    }

    [Fact]
    public void ExtractBalanceFromContent_SoldeAtEndOfString()
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent("Compte actif. Solde 7500");
        Assert.Equal(7500, result);
    }

    [Theory]
    [InlineData("Votre solde: 0,00 DZD", 0.00)]
    public void ExtractBalanceFromContent_ZeroWithDecimal_ReturnsZero(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent(content);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractBalanceFromContent_SingleZero_ReturnsNull()
    {
        var result = DatabaseWriteChannel.ExtractBalanceFromContent("Solde: 0 DZD");
        Assert.Null(result);
    }
}
