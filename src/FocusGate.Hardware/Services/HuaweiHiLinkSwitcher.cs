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

    public static async Task<int> DetectAndSwitchAsync(string[] gatewayIps, ILogger logger, CancellationToken ct)
    {
        var gateways = new List<string>();

        var macGateways = FindHuaweiGateways();
        gateways.AddRange(macGateways);
        logger.LogInformation("Found {Count} Huawei adapter(s) via MAC", macGateways.Count);

        foreach (var ip in gatewayIps)
        {
            if (!gateways.Contains(ip)) gateways.Add(ip);
        }

        if (gateways.Count == 0) return 0;

        logger.LogInformation("Scanning {Count} gateway(s) for Huawei HiLink...", gateways.Count);

        int switched = 0;
        foreach (var ip in gateways)
        {
            if (ct.IsCancellationRequested) break;
            var result = await ProbeAndSwitchAsync(ip, logger, ct);
            if (result) switched++;
        }

        return switched;
    }

    public static List<string> FindHuaweiGateways()
    {
        var gateways = new List<string>();

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

    private static async Task<bool> ProbeAndSwitchAsync(string ip, ILogger logger, CancellationToken ct)
    {
        try
        {
            var deviceInfo = await GetDeviceInfoAsync(ip, ct);
            if (deviceInfo == null) return false;

            logger.LogInformation("Huawei HiLink found at {Ip} — Model: {Model}, FW: {Firmware}",
                ip, deviceInfo.Model, deviceInfo.Firmware);

            var apiResult = await TryHiLinkApiSwitchAsync(ip, logger, ct);
            if (apiResult)
            {
                logger.LogInformation("Mode switch triggered via HiLink API on {Ip}", ip);
                return true;
            }

            logger.LogWarning("Cannot auto-switch {Ip} — opening web UI for manual switch", ip);
            OpenBrowserForManualSwitch(ip, deviceInfo, logger);
            return false;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            logger.LogDebug("HiLink probe {Ip} failed: {Error}", ip, ex.Message);
            return false;
        }
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

    private static async Task<bool> TryHiLinkApiSwitchAsync(string ip, ILogger logger, CancellationToken ct)
    {
        string[] apiPaths =
        [
            "/api/device/control",
            "/api/system/mode-switch",
            "/api/usb-switch"
        ];

        string[] payloads =
        [
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><request><Control>1</Control></request>",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><request><DataCard>modem</DataCard></request>",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><request><SwitchMode>1</SwitchMode></request>"
        ];

        foreach (var apiPath in apiPaths)
        {
            foreach (var payload in payloads)
            {
                try
                {
                    var url = $"http://{ip}{apiPath}";
                    using var content = new StringContent(payload, Encoding.UTF8, "application/xml");
                    using var response = await SharedClient.PostAsync(url, content, ct);

                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(ct);
                        if (body.Contains("OK") || body.Contains("success") || body.Contains("Success") || body.Length < 50)
                        {
                            logger.LogInformation("HiLink API {Path} responded on {Ip}", apiPath, ip);
                            return true;
                        }
                    }
                }
                catch { }
            }
        }

        return false;
    }

    private static void OpenBrowserForManualSwitch(string ip, DeviceInfo device, ILogger logger)
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

        logger.LogWarning("=== MANUAL SWITCH REQUIRED for Huawei {Model} (FW: {Firmware}) ===", device.Model, device.Firmware);
        logger.LogWarning("The modem is in HiLink mode (network adapter). To use as SMS modem:");
        logger.LogWarning("  1. A browser window opened to the modem web UI");
        logger.LogWarning("  2. Login if prompted (default password: admin)");
        logger.LogWarning("  3. Look for 'USB Mode' or 'Network Mode' in Settings");
        logger.LogWarning("  4. Change from 'HiLink' to 'Modem' or 'Stick' mode");
        logger.LogWarning("  5. The modem will reboot — wait 60 seconds");
        logger.LogWarning("  6. The app will detect the new COM port automatically");
        logger.LogWarning("");
        logger.LogWarning("If no USB Mode option exists, you need Huawei drivers:");
        logger.LogWarning("  1. Download Huawei Mobile Broadband drivers from huawei.com");
        logger.LogWarning("  2. Install the drivers (includes 'PC UI Interface' serial port)");
        logger.LogWarning("  3. Run the app again — it will detect the modem via AT commands");
    }

    private sealed class DeviceInfo
    {
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public string Firmware { get; set; } = "";
    }
}
