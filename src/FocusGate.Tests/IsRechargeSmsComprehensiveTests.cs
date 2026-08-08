using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class IsRechargeSmsComprehensiveTests
{
    [Theory]
    [InlineData("Vous avez reçu un montant de 500 DZD DA de 0555123456")]
    [InlineData("MONTANT DE 500 DZD REÇU DE 0661123456")]
    [InlineData("montant de 1000 reçu de 0770123456")]
    [InlineData("Le 676916007 vous a transféré un credit de 100 DA.")]
    [InlineData("Vous avez rechargé 300.00 DZD DA avec succès le 08/08/2026 07:58:57.")]
    [InlineData("Vous avez rechargé 2500.00 DZD DA avec succès le 08/08/2026 07:58:21.")]
    [InlineData("Vous avez reçu un montant de 300.00 DZD,numéro de la transaction est 04237200001270348022.")]
    [InlineData("Vous avez reçu un montant de 2500.00 DZD,numéro de la transaction est 04237500001270346825.")]
    public void IsRechargeSms_MontantDeAndRecu_ReturnsTrue(string content)
    {
        Assert.True(DatabaseWriteChannel.IsRechargeSms(content));
        Assert.NotNull(DatabaseWriteChannel.ExtractRechargeAmountFromContent(content));
    }

    [Theory]
    [InlineData("Montant de 500 DZD")]
    [InlineData("reçu de 500 DZD")]
    public void IsRechargeSms_MontantDeWithoutRecu_ReturnsFalse(string content)
    {
        Assert.False(DatabaseWriteChannel.IsRechargeSms(content));
    }

    [Theory]
    [InlineData("reçu un montant de 500")]
    [InlineData("Reçu montant de 500")]
    [InlineData("REÇU MONTANT DE 500")]
    public void IsRechargeSms_RecuBeforeMontantDe_ReturnsTrue(string content)
    {
        Assert.True(DatabaseWriteChannel.IsRechargeSms(content));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bonjour")]
    [InlineData("Solde: 5000 DZD")]
    [InlineData("Votre offre expire bientôt")]
    [InlineData("Bienvenue chez Mobilis")]
    public void IsRechargeSms_NoKeywords_ReturnsFalse(string content)
    {
        Assert.False(DatabaseWriteChannel.IsRechargeSms(content));
    }

    [Theory]
    [InlineData("montant de 500 reçu")]
    [InlineData("MONTANT DE 500 REÇU")]
    [InlineData("Montant De 500 Reçu")]
    public void IsRechargeSms_CaseInsensitive_ReturnsTrue(string content)
    {
        Assert.True(DatabaseWriteChannel.IsRechargeSms(content));
    }

    [Theory]
    [InlineData("montant de 500 recu de 0555123456")]
    [InlineData("montant de 500 recu")]
    public void IsRechargeSms_RecuWithoutCedilla_ReturnsFalse(string content)
    {
        Assert.False(DatabaseWriteChannel.IsRechargeSms(content));
    }

    [Fact]
    public void IsRechargeSms_MontantDeWithExtraWords_ReturnsTrue()
    {
        var content = "Cher client, vous avez reçu un montant de 500 DZD du numéro 0555123456";
        Assert.True(DatabaseWriteChannel.IsRechargeSms(content));
    }

    [Fact]
    public void IsRechargeSms_UnicodeArabicContent_ReturnsFalse()
    {
        var content = "تم إرسال مبلغ 500 دج";
        Assert.False(DatabaseWriteChannel.IsRechargeSms(content));
    }

    [Theory]
    [InlineData("montant de 500\nde 0555123456 reçu")]
    [InlineData("montant de 500\nreçu")]
    public void IsRechargeSms_Multiline_ReturnsTrue(string content)
    {
        Assert.True(DatabaseWriteChannel.IsRechargeSms(content));
    }

    [Fact]
    public void IsRechargeSms_VeryLongContent_ReturnsTrue()
    {
        var content = new string('x', 10000) + " montant de 500 reçu " + new string('y', 10000);
        Assert.True(DatabaseWriteChannel.IsRechargeSms(content));
    }
}
