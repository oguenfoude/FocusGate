using FocusGate.Core.Enums;
using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class DetectBrandComprehensiveTests
{
    [Theory]
    [InlineData("Ericsson", "F5521gw", ModemBrand.Ericsson)]
    [InlineData("ERICSSON", "B593", ModemBrand.Ericsson)]
    [InlineData("ericsson", "LM811", ModemBrand.Ericsson)]
    public void DetectBrand_Ericsson_ReturnsEricsson(string manufacturer, string model, ModemBrand expected)
    {
        Assert.Equal(expected, ModemHelper.DetectBrand(manufacturer, model));
    }

    [Theory]
    [InlineData("MediaTek", "MT6761", ModemBrand.MediaTek)]
    [InlineData("MEDIATEK", "MT6765", ModemBrand.MediaTek)]
    [InlineData("mediatek", "MT6580", ModemBrand.MediaTek)]
    public void DetectBrand_MediaTek_ReturnsMediaTek(string manufacturer, string model, ModemBrand expected)
    {
        Assert.Equal(expected, ModemHelper.DetectBrand(manufacturer, model));
    }

    [Theory]
    [InlineData("MediaTek", "MTK6761", ModemBrand.MediaTek)]
    [InlineData("MediaTek", "MTK MT6761", ModemBrand.MediaTek)]
    public void DetectBrand_MTKInModel_ReturnsMediaTek(string manufacturer, string model, ModemBrand expected)
    {
        Assert.Equal(expected, ModemHelper.DetectBrand(manufacturer, model));
    }

    [Theory]
    [InlineData(null, null, ModemBrand.Unknown)]
    [InlineData("", "", ModemBrand.Unknown)]
    [InlineData(null, "", ModemBrand.Unknown)]
    [InlineData("", null, ModemBrand.Unknown)]
    public void DetectBrand_NullOrEmpty_ReturnsUnknown(string? manufacturer, string? model, ModemBrand expected)
    {
        Assert.Equal(expected, ModemHelper.DetectBrand(manufacturer!, model!));
    }

    [Theory]
    [InlineData("RandomCorp", "XYZ123", ModemBrand.Other)]
    [InlineData("Acme", "ModemPro", ModemBrand.Other)]
    public void DetectBrand_UnrecognizedNonEmpty_ReturnsOther(string manufacturer, string model, ModemBrand expected)
    {
        Assert.Equal(expected, ModemHelper.DetectBrand(manufacturer, model));
    }

    [Fact]
    public void DetectBrand_MatchInModel_HuaweiModel()
    {
        var result = ModemHelper.DetectBrand("SomeUnknown", "Huawei E3372");
        Assert.Equal(ModemBrand.Huawei, result);
    }

    [Fact]
    public void DetectBrand_MatchInManufacturer_Alaafi()
    {
        var result = ModemHelper.DetectBrand("Alaafi", "Custom Box");
        Assert.Equal(ModemBrand.Alaafi, result);
    }

    [Fact]
    public void DetectBrand_MatchInModel_FlexiDZ()
    {
        var result = ModemHelper.DetectBrand("Unknown", "FlexiDZ Box v2");
        Assert.Equal(ModemBrand.FlexiDZ, result);
    }

    [Fact]
    public void DetectBrand_MatchInModel_Flixi()
    {
        var result = ModemHelper.DetectBrand("Unknown", "Flixi Box");
        Assert.Equal(ModemBrand.FlexiDZ, result);
    }

    [Fact]
    public void DetectBrand_ZTEInModel_NotManufacturer()
    {
        var result = ModemHelper.DetectBrand("SomeCorp", "ZTE MF833V");
        Assert.Equal(ModemBrand.ZTE, result);
    }

    [Fact]
    public void DetectBrand_SierraInModel_NotManufacturer()
    {
        var result = ModemHelper.DetectBrand("SomeCorp", "Sierra MC7455");
        Assert.Equal(ModemBrand.SierraWireless, result);
    }

    [Fact]
    public void DetectBrand_Priority_AlaafiOverHuawei()
    {
        var result = ModemHelper.DetectBrand("Huawei", "Alaafi Box");
        Assert.Equal(ModemBrand.Alaafi, result);
    }

    [Fact]
    public void DetectBrand_Priority_FlexiDZOverZTE()
    {
        var result = ModemHelper.DetectBrand("ZTE", "FlexiDZ Box");
        Assert.Equal(ModemBrand.FlexiDZ, result);
    }

    [Fact]
    public void DetectBrand_CaseInsensitive_PartialMatch()
    {
        var result = ModemHelper.DetectBrand("HUAWEI TECHNOLOGIES", "E3372");
        Assert.Equal(ModemBrand.Huawei, result);
    }
}
