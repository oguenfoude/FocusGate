using FocusGate.Core.DTOs;
using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class IsMobilisBalanceTriggerTests
{
    private static RawSmsMessage Sms(string sender, string content) =>
        new() { Sender = sender, Content = content };

    [Theory]
    [InlineData("Mobilis")]
    [InlineData("77111")]
    [InlineData("610")]
    public void IsMobilisBalanceTrigger_KnownSenders_RechargeContent_ReturnsTrue(string sender)
    {
        var sms = Sms(sender, "Vous avez re\u00e7u un montant de 500 DZD DA de 0555123456");
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Theory]
    [InlineData(" Mobilis ")]
    [InlineData("Mobilis ")]
    [InlineData(" Mobilis")]
    [InlineData("\tMobilis\t")]
    [InlineData("\nMobilis\n")]
    public void IsMobilisBalanceTrigger_WhitespaceSender_TrimmedMatches(string sender)
    {
        var sms = Sms(sender, "recharg\u00e9 avec succ\u00e8s 500 DZD");
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Theory]
    [InlineData("orange")]
    [InlineData("Orange")]
    [InlineData("DJEZZY")]
    [InlineData("ooredoo")]
    [InlineData("Ooredoo")]
    [InlineData("+213555123456")]
    [InlineData("0555123456")]
    [InlineData("555123456")]
    [InlineData("foo bar")]
    public void IsMobilisBalanceTrigger_UnknownSender_ReturnsFalse(string sender)
    {
        var sms = Sms(sender, "Vous avez re\u00e7u un montant de 500 DZD DA");
        Assert.False(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Theory]
    [InlineData("Mobilis", "")]
    [InlineData("Mobilis", "Bonjour")]
    [InlineData("Mobilis", "Solde: 5000 DZD")]
    [InlineData("Mobilis", "Votre offre expire bient\u00f4t")]
    [InlineData("Mobilis", "Votre forfait est renouvel\u00e9")]
    [InlineData("Mobilis", "Bienvenue chez Mobilis")]
    public void IsMobilisBalanceTrigger_NoRechargeKeywords_ReturnsFalse(string sender, string content)
    {
        var sms = Sms(sender, content);
        Assert.False(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Theory]
    [InlineData("Vous avez re\u00e7u un montant de 500 DZD")]
    [InlineData("MONTANT DE 500 DZD RE\u00c7U DE 0555123456")]
    [InlineData("re\u00e7u un montant de 1000 DZD")]
    public void IsMobilisBalanceTrigger_MontantDeRecu_ReturnsTrue(string content)
    {
        var sms = Sms("Mobilis", content);
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Theory]
    [InlineData("Vous avez recharg\u00e9 500 DZD avec succ\u00e8s")]
    [InlineData("RECHARG\u00c9 1000 DZD")]
    public void IsMobilisBalanceTrigger_RechargeAccented_ReturnsTrue(string content)
    {
        var sms = Sms("Mobilis", content);
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Theory]
    [InlineData("recharge effectuee 250 DZD")]
    [InlineData("RECHARGE 500 DZD")]
    public void IsMobilisBalanceTrigger_RechargeNoAccent_ReturnsFalse(string content)
    {
        var sms = Sms("Mobilis", content);
        Assert.False(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Theory]
    [InlineData("vous avez recu un montant de 1000 DZD")]
    [InlineData("RECU montant de 500")]
    public void IsMobilisBalanceTrigger_RecuNoCedilla_ReturnsFalse(string content)
    {
        var sms = Sms("Mobilis", content);
        Assert.False(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Fact]
    public void IsMobilisBalanceTrigger_OnlyMontantDeWithoutRecu_ReturnsFalse()
    {
        var sms = Sms("Mobilis", "montant de 500 DZD");
        Assert.False(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Fact]
    public void IsMobilisBalanceTrigger_CaseInsensitive_MontantDe()
    {
        var sms = Sms("Mobilis", "MONTANT DE 500 RE\u00c7U");
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Fact]
    public void IsMobilisBalanceTrigger_CaseInsensitive_Recharge()
    {
        var sms = Sms("Mobilis", "RECHARG\u00c9 500");
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Fact]
    public void IsMobilisBalanceTrigger_TransferFrom77111_ReturnsTrue()
    {
        var sms = Sms("77111", "Vous avez re\u00e7u un montant de 500 DZD");
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Fact]
    public void IsMobilisBalanceTrigger_RechargeFrom610_ReturnsTrue()
    {
        var sms = Sms("610", "Vous avez recharg\u00e9 500 DZD");
        Assert.True(ModemHandler.IsMobilisBalanceTrigger(sms));
    }

    [Theory]
    [InlineData("Mobilis123")]
    [InlineData("123Mobilis")]
    [InlineData("Mobiliss")]
    [InlineData("6100")]
    [InlineData("771110")]
    [InlineData("")]
    public void IsMobilisBalanceTrigger_SimilarButNotExactSender_ReturnsFalse(string sender)
    {
        var sms = Sms(sender, "Vous avez re\u00e7u un montant de 500 DZD");
        Assert.False(ModemHandler.IsMobilisBalanceTrigger(sms));
    }
}
