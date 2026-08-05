using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class ClassifySmsTypeEdgeTests
{
    [Theory]
    [InlineData("Mobilis", "SOLDE: 5000 DZD", "balance")]
    [InlineData("Mobilis", "solde de votre compte", "balance")]
    [InlineData("77111", "Solde Disponible", "balance")]
    public void ClassifySmsType_BalanceKeywords_ReturnsBalance(string sender, string content, string expected)
    {
        var result = DatabaseWriteChannel.ClassifySmsType(sender, content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Mobilis", "montant de 500 reçu", "transfer")]
    [InlineData("Mobilis", "Montant De 1000 DZD Reçu", "transfer")]
    public void ClassifySmsType_TransferKeywords_ReturnsTransfer(string sender, string content, string expected)
    {
        var result = DatabaseWriteChannel.ClassifySmsType(sender, content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Mobilis", "rechargé avec succès", "recharge")]
    [InlineData("Mobilis", "RECHARGÉ 500 DZD", "recharge")]
    public void ClassifySmsType_RechargeKeywords_ReturnsRecharge(string sender, string content, string expected)
    {
        var result = DatabaseWriteChannel.ClassifySmsType(sender, content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Mobilis", "Votre offre expire bientôt", "offer")]
    [InlineData("Mobilis", "votre offre a été renouvelée", "offer")]
    public void ClassifySmsType_OfferKeywords_ReturnsOffer(string sender, string content, string expected)
    {
        var result = DatabaseWriteChannel.ClassifySmsType(sender, content);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ClassifySmsType_Sender610_Matches()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("610", "Solde: 5000 DZD");
        Assert.Equal("balance", result);
    }

    [Fact]
    public void ClassifySmsType_Sender77111_Matches()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("77111", "Solde: 5000 DZD");
        Assert.Equal("balance", result);
    }

    [Fact]
    public void ClassifySmsType_BothBalanceAndTransfer_ReturnsBalance()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("Mobilis", "Solde: 5000 DZD, montant de 500 reçu");
        Assert.Equal("balance", result);
    }

    [Fact]
    public void ClassifySmsType_BothBalanceAndRecharge_ReturnsBalance()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("Mobilis", "Solde: 5000 DZD, rechargé avec succès");
        Assert.Equal("balance", result);
    }

    [Fact]
    public void ClassifySmsType_TransferAndRecharge_ReturnsTransfer()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("Mobilis", "montant de 500 reçu, rechargé avec succès");
        Assert.Equal("transfer", result);
    }

    [Fact]
    public void ClassifySmsType_EmptySender_ReturnsOther()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("", "Solde: 5000 DZD");
        Assert.Equal("other", result);
    }

    [Fact]
    public void ClassifySmsType_WhitespaceContent_ReturnsMobilisOther()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("Mobilis", "   ");
        Assert.Equal("mobilis-other", result);
    }

    [Fact]
    public void ClassifySmsType_OnlySoldeKeyword_ReturnsBalance()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("Mobilis", "Solde");
        Assert.Equal("balance", result);
    }

    [Fact]
    public void ClassifySmsType_OnlyRechargeKeyword_ReturnsRecharge()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("Mobilis", "rechargé");
        Assert.Equal("recharge", result);
    }
}
