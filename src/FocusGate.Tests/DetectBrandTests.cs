using FocusGate.Core.Enums;
using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class DetectBrandTests
{
    [Theory]
    [InlineData("Huawei", "E3531", ModemBrand.Huawei)]
    [InlineData("huawei", "E3372", ModemBrand.Huawei)]
    [InlineData("HUAWEI", "B315", ModemBrand.Huawei)]
    public void DetectBrand_Huawei_ReturnsHuawei(string manufacturer, string model, ModemBrand expected)
    {
        var result = ModemHelper.DetectBrand(manufacturer, model);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("ZTE", "MF833V", ModemBrand.ZTE)]
    [InlineData("zte", "MF823", ModemBrand.ZTE)]
    public void DetectBrand_ZTE_ReturnsZTE(string manufacturer, string model, ModemBrand expected)
    {
        var result = ModemHelper.DetectBrand(manufacturer, model);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Quectel", "EC25", ModemBrand.Quectel)]
    [InlineData("quectel", "EP06", ModemBrand.Quectel)]
    public void DetectBrand_Quectel_ReturnsQuectel(string manufacturer, string model, ModemBrand expected)
    {
        var result = ModemHelper.DetectBrand(manufacturer, model);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("SIMCom", "SIM7600", ModemBrand.SIMCom)]
    [InlineData("simcom", "SIM5360", ModemBrand.SIMCom)]
    public void DetectBrand_SIMCom_ReturnsSIMCom(string manufacturer, string model, ModemBrand expected)
    {
        var result = ModemHelper.DetectBrand(manufacturer, model);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Sierra Wireless", "MC7455", ModemBrand.SierraWireless)]
    [InlineData("sierra", "EM7455", ModemBrand.SierraWireless)]
    public void DetectBrand_Sierra_ReturnsSierra(string manufacturer, string model, ModemBrand expected)
    {
        var result = ModemHelper.DetectBrand(manufacturer, model);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Alaafi", "Custom", ModemBrand.Alaafi)]
    [InlineData("FlexiDZ", "Box", ModemBrand.FlexiDZ)]
    public void DetectBrand_LocalBrands_ReturnsCorrect(string manufacturer, string model, ModemBrand expected)
    {
        var result = ModemHelper.DetectBrand(manufacturer, model);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("", "", ModemBrand.Unknown)]
    public void DetectBrand_Empty_ReturnsUnknown(string manufacturer, string model, ModemBrand expected)
    {
        var result = ModemHelper.DetectBrand(manufacturer, model);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("UnknownVendor", "GenericModem", ModemBrand.Other)]
    public void DetectBrand_Unrecognized_ReturnsOther(string manufacturer, string model, ModemBrand expected)
    {
        var result = ModemHelper.DetectBrand(manufacturer, model);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DetectBrand_CaseInsensitive()
    {
        var result = ModemHelper.DetectBrand("HUAWEI", "e3531");
        Assert.Equal(ModemBrand.Huawei, result);
    }
}
