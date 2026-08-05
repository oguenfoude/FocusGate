using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class ExtractRechargeAmountEdgeTests
{
    [Theory]
    [InlineData("montant de 500 DZD reçu de 0555123456", 500)]
    [InlineData("montant de 1000 reçu de 0661123456", 1000)]
    [InlineData("montant de 150,50 DZD reçu", 150.50)]
    public void ExtractRechargeAmount_MontantDe_ReturnsAmount(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("montant de un 250 DZD reçu", 250)]
    [InlineData("montant de un 1000 reçu", 1000)]
    public void ExtractRechargeAmount_MontantDeUn_ReturnsAmount(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("rechargé 500 DZD", 500)]
    [InlineData("rechargé 1000 DZD avec succès", 1000)]
    [InlineData("RECHARGÉ 250", 250)]
    public void ExtractRechargeAmount_Recharge_ReturnsAmount(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("rechargé de 500 DZD", 500)]
    [InlineData("rechargé de 1000", 1000)]
    public void ExtractRechargeAmount_RechargeDe_ReturnsAmount(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Solde: 5000 DZD", 5000)]
    [InlineData("Votre solde est 12500 DA", 12500)]
    public void ExtractRechargeAmount_FallbackDZD_ReturnsAmount(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bonjour")]
    [InlineData("Votre offre expire bientôt")]
    [InlineData("Bienvenue chez Mobilis")]
    public void ExtractRechargeAmount_NoAmount_ReturnsNull(string content)
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("montant de 2.500,75 DZD reçu", 2500.75)]
    [InlineData("rechargé 1.234,56 DZD", 1234.56)]
    public void ExtractRechargeAmount_EuropeanFormat_ReturnsAmount(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractRechargeAmount_CaseInsensitive()
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent("MONTANT DE 500 REÇU");
        Assert.NotNull(result);
        Assert.Equal(500, result);
    }

    [Theory]
    [InlineData("montant de 500 DZD reçu", 500)]
    [InlineData("montant de 500 DA reçu", 500)]
    [InlineData("montant de 500 dzd reçu", 500)]
    public void ExtractRechargeAmount_MultipleCurrencySuffixes(string content, decimal expected)
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent(content);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtractRechargeAmount_UnicodeAccents()
    {
        var result = DatabaseWriteChannel.ExtractRechargeAmountFromContent("Vous avez re\u00e7u un montant de 500 DZD");
        Assert.NotNull(result);
        Assert.Equal(500, result);
    }
}
