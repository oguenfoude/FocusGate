using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class ClassifySmsTypeSpecialCharTests
{
    [Theory]
    [InlineData("Mobilis", "Solde: 5000 DZD \u2014 rechargé", "balance")]
    [InlineData("Mobilis", "Votre solde est 12500 DA.", "balance")]
    public void ClassifySmsType_SpecialCharsInContent_ReturnsBalance(string sender, string content, string expected)
    {
        Assert.Equal(expected, DatabaseWriteChannel.ClassifySmsType(sender, content));
    }

    [Theory]
    [InlineData("Mobilis", "montant de 500 re\u00e7u de 0555", "transfer")]
    [InlineData("Mobilis", "MONTANT DE 500 RE\u00c7U", "transfer")]
    public void ClassifySmsType_UnicodeAccents_Transfer(string sender, string content, string expected)
    {
        Assert.Equal(expected, DatabaseWriteChannel.ClassifySmsType(sender, content));
    }

    [Theory]
    [InlineData("Mobilis", "recharg\u00e9 avec succ\u00e8s 500", "recharge")]
    [InlineData("77111", "RECHARG\u00c9 1000 DZD", "recharge")]
    public void ClassifySmsType_UnicodeAccents_Recharge(string sender, string content, string expected)
    {
        Assert.Equal(expected, DatabaseWriteChannel.ClassifySmsType(sender, content));
    }

    [Theory]
    [InlineData("Mobilis", "Votre offre expire bient\u00f4t", "offer")]
    [InlineData("Mobilis", "Votre OFFRE sp\u00e9ciale", "offer")]
    public void ClassifySmsType_UnicodeAccents_Offer(string sender, string content, string expected)
    {
        Assert.Equal(expected, DatabaseWriteChannel.ClassifySmsType(sender, content));
    }

    [Fact]
    public void ClassifySmsType_VeryLongContent_ClassifiesCorrectly()
    {
        var content = new string('x', 10000) + " Solde: 5000 DZD " + new string('y', 10000);
        Assert.Equal("balance", DatabaseWriteChannel.ClassifySmsType("Mobilis", content));
    }

    [Fact]
    public void ClassifySmsType_MultipleKeywords_BalanceWins()
    {
        var content = "Solde: 5000 DZD, montant de 500 re\u00e7u, recharg\u00e9 300";
        Assert.Equal("balance", DatabaseWriteChannel.ClassifySmsType("Mobilis", content));
    }

    [Fact]
    public void ClassifySmsType_TransferAndRecharge_TransferWins()
    {
        var content = "montant de 500 re\u00e7u, recharg\u00e9 300";
        Assert.Equal("transfer", DatabaseWriteChannel.ClassifySmsType("Mobilis", content));
    }

    [Fact]
    public void ClassifySmsType_RechargeAndOffer_RechargeWins()
    {
        var content = "recharg\u00e9 300, votre offre expire";
        Assert.Equal("recharge", DatabaseWriteChannel.ClassifySmsType("Mobilis", content));
    }

    [Theory]
    [InlineData("Mobilis", "\n\n\n")]
    [InlineData("Mobilis", "\t\t\t")]
    [InlineData("Mobilis", "      ")]
    public void ClassifySmsType_WhitespaceOnly_ReturnsMobilisOther(string sender, string content)
    {
        Assert.Equal("mobilis-other", DatabaseWriteChannel.ClassifySmsType(sender, content));
    }

    [Theory]
    [InlineData("610", "Solde: 5000 DZD", "balance")]
    [InlineData("610", "montant de 500 re\u00e7u", "transfer")]
    [InlineData("610", "recharg\u00e9 500", "recharge")]
    [InlineData("610", "Votre offre", "offer")]
    public void ClassifySmsType_Sender610_AllCategories(string sender, string content, string expected)
    {
        Assert.Equal(expected, DatabaseWriteChannel.ClassifySmsType(sender, content));
    }

    [Theory]
    [InlineData("77111", "Solde: 5000 DZD", "balance")]
    [InlineData("77111", "montant de 500 re\u00e7u", "transfer")]
    [InlineData("77111", "recharg\u00e9 500", "recharge")]
    [InlineData("77111", "Votre offre", "offer")]
    public void ClassifySmsType_Sender77111_AllCategories(string sender, string content, string expected)
    {
        Assert.Equal(expected, DatabaseWriteChannel.ClassifySmsType(sender, content));
    }
}
