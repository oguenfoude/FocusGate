#pragma warning disable CA1416
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

public class MachineInfoService
{
    private readonly string _machineId;
    private readonly string _machineName;
    private readonly string _osInfo;
    private readonly string _username;

    public string MachineId => _machineId;

    public MachineInfoService(ILogger<MachineInfoService> logger)
    {
        _machineName = Environment.MachineName;
        _username = Environment.UserName;
        _osInfo = Environment.OSVersion.ToString();

        var fingerprint = new StringBuilder();
        fingerprint.Append(_machineName);
        fingerprint.Append(_username);

        try
        {
            var mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetPhysicalAddress().ToString())
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(mac))
                fingerprint.Append(mac);
        }
        catch { }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid")?.ToString();
            if (!string.IsNullOrEmpty(guid))
                fingerprint.Append(guid);
        }
        catch { }

        _machineId = BitConverter.ToString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())))
            .Replace("-", "")[..16].ToLowerInvariant();

        logger.LogInformation("Machine: {Id} ({Name}@{User}, {OS})",
            _machineId, _machineName, _username, _osInfo);
    }
}
