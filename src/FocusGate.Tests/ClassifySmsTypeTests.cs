using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class ClassifySmsTypeTests
{
    [Theory]
    [InlineData("Mobilis", "Solde de votre compte: 5000 DZD", "balance")]
    [InlineData("77111", "Votre solde est 12500 DA", "balance")]
    [InlineData("610", "SOLDE: 350,75 DZD", "balance")]
    public void ClassifySmsType_BalanceContent_ReturnsBalance(string sender, string content, string expected)
    {
        var result = DatabaseWriteChannel.ClassifySmsType(sender, content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Mobilis", "Vous avez reçu un montant de 500 DZD DA de 0555123456", "transfer")]
    [InlineData("77111", "montant de 1000 DZD reçu de 0661123456", "transfer")]
    public void ClassifySmsType_TransferContent_ReturnsTransfer(string sender, string content, string expected)
    {
        var result = DatabaseWriteChannel.ClassifySmsType(sender, content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Mobilis", "Vous avez rechargé 1000 DZD DA au 0555123456", "recharge")]
    [InlineData("77111", "rechargé 300 DZD", "recharge")]
    public void ClassifySmsType_RechargeContent_ReturnsRecharge(string sender, string content, string expected)
    {
        var result = DatabaseWriteChannel.ClassifySmsType(sender, content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Mobilis", "Votre offre a expiré", "offer")]
    [InlineData("77111", "Offre spéciale disponible", "offer")]
    public void ClassifySmsType_OfferContent_ReturnsOffer(string sender, string content, string expected)
    {
        var result = DatabaseWriteChannel.ClassifySmsType(sender, content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Mobilis", "Message promotionnel spécial", "mobilis-other")]
    [InlineData("77111", "Info: nouveau forfait disponible", "mobilis-other")]
    public void ClassifySmsType_MobilisOther_ReturnsMobilisOther(string sender, string content, string expected)
    {
        var result = DatabaseWriteChannel.ClassifySmsType(sender, content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Orange", "Votre solde est 5000 DZD", "other")]
    [InlineData("12345", "Random message", "other")]
    [InlineData("", "Empty sender", "other")]
    public void ClassifySmsType_NonMobilis_ReturnsOther(string sender, string content, string expected)
    {
        var result = DatabaseWriteChannel.ClassifySmsType(sender, content);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ClassifySmsType_CaseInsensitive()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("Mobilis", "SOLDE: 5000 DZD");
        Assert.Equal("balance", result);
    }

    [Fact]
    public void ClassifySmsType_EmptyContent_ReturnsMobilisOther()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("Mobilis", "");
        Assert.Equal("mobilis-other", result);
    }

    [Fact]
    public void ClassifySmsType_SenderNotTrimmed_RequiresExactMatch()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("  Mobilis  ", "Solde: 5000 DZD");
        Assert.Equal("other", result);
    }

    [Fact]
    public void ClassifySmsType_ForfaitWithoutOffre_ReturnsMobilisOther()
    {
        var result = DatabaseWriteChannel.ClassifySmsType("77111", "Votre forfait expire bientôt");
        Assert.Equal("mobilis-other", result);
    }
}
