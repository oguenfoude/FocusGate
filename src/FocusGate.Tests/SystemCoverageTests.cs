using FocusGate.Core.Enums;
using FocusGate.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace FocusGate.Tests;

public class ModemHandlerPropertyTests
{
    [Fact]
    public void DetectBrand_AllEnumValues_Covered()
    {
        var brands = Enum.GetValues(typeof(ModemBrand)).Cast<ModemBrand>();
        Assert.Contains(ModemBrand.Unknown, brands);
        Assert.Contains(ModemBrand.ZTE, brands);
        Assert.Contains(ModemBrand.Huawei, brands);
        Assert.Contains(ModemBrand.Quectel, brands);
        Assert.Contains(ModemBrand.SIMCom, brands);
        Assert.Contains(ModemBrand.SierraWireless, brands);
        Assert.Contains(ModemBrand.Ericsson, brands);
        Assert.Contains(ModemBrand.MediaTek, brands);
        Assert.Contains(ModemBrand.Alaafi, brands);
        Assert.Contains(ModemBrand.FlexiDZ, brands);
        Assert.Contains(ModemBrand.Other, brands);
    }

    [Fact]
    public void DetectBrand_EachBrand_UniqueEnumValue()
    {
        var brands = Enum.GetValues(typeof(ModemBrand)).Cast<ModemBrand>().ToList();
        var values = brands.Select(b => (int)b).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Theory]
    [InlineData("Huawei", "E3531", ModemBrand.Huawei)]
    [InlineData("ZTE", "MF833V", ModemBrand.ZTE)]
    [InlineData("Quectel", "EC25", ModemBrand.Quectel)]
    [InlineData("SIMCom", "SIM7600", ModemBrand.SIMCom)]
    [InlineData("Sierra Wireless", "MC7455", ModemBrand.SierraWireless)]
    [InlineData("Ericsson", "F5521gw", ModemBrand.Ericsson)]
    [InlineData("MediaTek", "MT6761", ModemBrand.MediaTek)]
    [InlineData("Alaafi", "Custom", ModemBrand.Alaafi)]
    [InlineData("FlexiDZ", "Box", ModemBrand.FlexiDZ)]
    public void DetectBrand_AllBrands_CoveredByTests(string manufacturer, string model, ModemBrand expected)
    {
        Assert.Equal(expected, ModemHelper.DetectBrand(manufacturer, model));
    }

    [Theory]
    [InlineData("huawei", "e3531")]
    [InlineData("HUAWEI", "E3372")]
    [InlineData("zte", "mf833v")]
    [InlineData("ZTE", "MF833V")]
    [InlineData("quectel", "ec25")]
    [InlineData("QUECTEL", "EC25")]
    [InlineData("simcom", "sim7600")]
    [InlineData("SIMCOM", "SIM7600")]
    [InlineData("sierra", "mc7455")]
    [InlineData("SIERRA", "MC7455")]
    [InlineData("ericsson", "f5521gw")]
    [InlineData("ERICSSON", "F5521GW")]
    [InlineData("mediatek", "mt6761")]
    [InlineData("MEDIATEK", "MT6761")]
    public void DetectBrand_AllBrands_CaseInsensitive(string manufacturer, string model)
    {
        var expected = ModemHelper.DetectBrand(manufacturer.ToUpperInvariant(), model.ToUpperInvariant());
        var actual = ModemHelper.DetectBrand(manufacturer.ToLowerInvariant(), model.ToLowerInvariant());
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ClassifySmsType_AllSenders_Covered()
    {
        var mobilisSenders = new[] { "Mobilis", "77111", "610" };
        foreach (var sender in mobilisSenders)
        {
            Assert.NotEqual("other", DatabaseWriteChannel.ClassifySmsType(sender, "test"));
        }
        Assert.Equal("other", DatabaseWriteChannel.ClassifySmsType("Orange", "test"));
        Assert.Equal("other", DatabaseWriteChannel.ClassifySmsType("", "test"));
    }

    [Fact]
    public void ClassifySmsType_AllCategories_Covered()
    {
        Assert.Equal("balance", DatabaseWriteChannel.ClassifySmsType("Mobilis", "Solde: 5000"));
        Assert.Equal("transfer", DatabaseWriteChannel.ClassifySmsType("Mobilis", "montant de 500 re\u00e7u"));
        Assert.Equal("recharge", DatabaseWriteChannel.ClassifySmsType("Mobilis", "recharg\u00e9 500"));
        Assert.Equal("offer", DatabaseWriteChannel.ClassifySmsType("Mobilis", "Votre offre"));
        Assert.Equal("mobilis-other", DatabaseWriteChannel.ClassifySmsType("Mobilis", "Hello"));
        Assert.Equal("other", DatabaseWriteChannel.ClassifySmsType("Orange", "Solde: 5000"));
    }

    [Fact]
    public void IsRechargeSms_BothKeywordsRequired()
    {
        Assert.False(DatabaseWriteChannel.IsRechargeSms("montant de 500"));
        Assert.False(DatabaseWriteChannel.IsRechargeSms("re\u00e7u"));
        Assert.True(DatabaseWriteChannel.IsRechargeSms("montant de 500 re\u00e7u"));
    }

    [Fact]
    public void ExtractBalanceFromContent_SoldeRequired()
    {
        Assert.Null(DatabaseWriteChannel.ExtractBalanceFromContent("Votre compte: 5000 DZD"));
        Assert.NotNull(DatabaseWriteChannel.ExtractBalanceFromContent("Solde: 5000 DZD"));
    }

    [Fact]
    public void ParseAmount_AllFormats_Covered()
    {
        Assert.Equal(100, DatabaseWriteChannel.ParseAmount("100"));
        Assert.Equal(100.50m, DatabaseWriteChannel.ParseAmount("100,50"));
        Assert.Equal(100.50m, DatabaseWriteChannel.ParseAmount("100.50"));
        Assert.Equal(1000, DatabaseWriteChannel.ParseAmount("1.000"));
        Assert.Equal(1000.50m, DatabaseWriteChannel.ParseAmount("1.000,50"));
        Assert.Equal(1000.50m, DatabaseWriteChannel.ParseAmount("1,000.50"));
        Assert.Null(DatabaseWriteChannel.ParseAmount("abc"));
    }
}
