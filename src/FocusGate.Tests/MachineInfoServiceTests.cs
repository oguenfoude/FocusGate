using FocusGate.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace FocusGate.Tests;

public class MachineInfoServiceTests
{
    [Fact]
    public void MachineId_IsNotEmpty()
    {
        var logger = new LoggerFactory().CreateLogger<MachineInfoService>();
        var service = new MachineInfoService(logger);
        Assert.False(string.IsNullOrEmpty(service.MachineId));
    }

    [Fact]
    public void MachineId_Is16Characters()
    {
        var logger = new LoggerFactory().CreateLogger<MachineInfoService>();
        var service = new MachineInfoService(logger);
        Assert.Equal(16, service.MachineId.Length);
    }

    [Fact]
    public void MachineId_IsLowercaseHex()
    {
        var logger = new LoggerFactory().CreateLogger<MachineInfoService>();
        var service = new MachineInfoService(logger);
        Assert.Matches("^[0-9a-f]{16}$", service.MachineId);
    }

    [Fact]
    public void MachineId_IsDeterministic()
    {
        var logger = new LoggerFactory().CreateLogger<MachineInfoService>();
        var service1 = new MachineInfoService(logger);
        var service2 = new MachineInfoService(logger);
        Assert.Equal(service1.MachineId, service2.MachineId);
    }
}
