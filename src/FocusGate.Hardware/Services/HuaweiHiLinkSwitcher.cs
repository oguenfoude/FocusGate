using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace FocusGate.Hardware.Services;

public static class HuaweiHiLinkSwitcher
{
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly string[] HuaweiMacPrefixes =
    [
        "00-1E-10", "00-1E-2E", "00-25-68", "00-46-4B",
        "20-08-49", "20-2B-C1", "2C-AB-00", "30-D1-7E",
        "48-46-FB", "4C-8B-EF", "5C-7D-5E", "70-72-3C",
        "88-28-B3", "8C-A5-A1", "AC-CF-85", "B4-15-13",
        "C0-70-09", "CC-53-B5", "D0-7A-B5", "E0-24-7F",
        "F4-55-9C", "F4-C7-14"
    ];

    public static HashSet<string> FindHuaweiGateways()
    {
        var gateways = new HashSet<string>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                var mac = ni.GetPhysicalAddress().ToString();
                if (string.IsNullOrEmpty(mac) || mac.Length < 6) continue;

                var macFormatted = FormatMac(mac);
                var isHuawei = HuaweiMacPrefixes.Any(p =>
                    macFormatted.StartsWith(p, StringComparison.OrdinalIgnoreCase));

                if (!isHuawei) continue;

                var gatewayIp = ni.GetIPProperties().GatewayAddresses
                    .FirstOrDefault(g =>
                        g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !g.Address.Equals(System.Net.IPAddress.Loopback))?.Address.ToString();

                if (!string.IsNullOrEmpty(gatewayIp))
                    gateways.Add(gatewayIp);
            }
        }
        catch { }

        return gateways;
    }

    private static string FormatMac(string raw)
    {
        var clean = raw.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (clean.Length < 12) return raw;
        return $"{clean[0..2]}-{clean[2..4]}-{clean[4..6]}-{clean[6..8]}-{clean[8..10]}-{clean[10..12]}";
    }

    public static async Task<int> DetectAndOpenBrowsersAsync(ILogger logger, CancellationToken ct)
    {
        var gateways = FindHuaweiGateways();
        if (gateways.Count == 0)
        {
            logger.LogDebug("No Huawei adapters detected via MAC");
            return 0;
        }

        logger.LogInformation("Found {Count} Huawei adapter(s) via MAC", gateways.Count);

        int opened = 0;
        foreach (var ip in gateways)
        {
            if (ct.IsCancellationRequested) break;

            var deviceInfo = await GetDeviceInfoAsync(ip, ct);
            if (deviceInfo == null) continue;

            logger.LogWarning("Huawei HiLink detected at {Ip} — Model: {Model}, FW: {Firmware}", ip, deviceInfo.Model, deviceInfo.Firmware);
            OpenBrowser(ip, logger);

            logger.LogWarning("=== MANUAL SWITCH REQUIRED for Huawei {Model} ===", deviceInfo.Model);
            logger.LogWarning("The modem is in HiLink mode (no COM ports). To use as SMS modem:");
            logger.LogWarning("  1. A browser window opened to the modem web UI");
            logger.LogWarning("  2. Login if prompted (default password: admin)");
            logger.LogWarning("  3. Look for 'USB Mode' or 'Network Mode' in Settings");
            logger.LogWarning("  4. Change from 'HiLink' to 'Modem' or 'Stick' mode");
            logger.LogWarning("  5. The modem will reboot — wait 60 seconds");
            logger.LogWarning("  6. The app will detect the new COM port automatically");
            logger.LogWarning("If no USB Mode option exists, install Huawei Mobile Broadband drivers from huawei.com");

            opened++;
        }

        return opened;
    }

    private static async Task<DeviceInfo?> GetDeviceInfoAsync(string ip, CancellationToken ct)
    {
        try
        {
            var url = $"http://{ip}/api/device/information";
            using var response = await SharedClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(body);
            var manufacturer = doc.Root?.Element("manufacturer")?.Value ?? "";
            if (!manufacturer.Contains("HUAWEI", StringComparison.OrdinalIgnoreCase)) return null;

            return new DeviceInfo
            {
                Manufacturer = manufacturer,
                Model = doc.Root?.Element("modelname")?.Value ?? "unknown",
                Firmware = doc.Root?.Element("version")?.Value ?? "unknown"
            };
        }
        catch { }

        try
        {
            var url = $"http://{ip}/html/home.html";
            using var response = await SharedClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!body.Contains("HUAWEI", StringComparison.OrdinalIgnoreCase)
                && !body.Contains("HiLink", StringComparison.OrdinalIgnoreCase))
                return null;

            return new DeviceInfo { Manufacturer = "HUAWEI", Model = "unknown", Firmware = "unknown" };
        }
        catch { }

        return null;
    }

    private static void OpenBrowser(string ip, ILogger logger)
    {
        var url = $"http://{ip}/html/index.html";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
                Verb = "open",
                WindowStyle = ProcessWindowStyle.Normal
            });
            logger.LogWarning("Opened modem web UI: {Url}", url);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not open browser: {Error}", ex.Message);
        }
    }

    private sealed class DeviceInfo
    {
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public string Firmware { get; set; } = "";
    }
}
