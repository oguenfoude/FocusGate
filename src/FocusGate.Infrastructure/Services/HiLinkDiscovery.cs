using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

public class HiLinkDiscovery
{
    private readonly ILogger<HiLinkDiscovery> _log;
    private static readonly XNamespace Ns = "http://schemas.datacontract.org/2004/07/Huawei.Hilink.DataModel";

    private static readonly string[] DefaultHiLinkIps = new[]
    {
        "192.168.8.1",
        "192.168.200.1",
        "192.168.1.1"
    };

    public HiLinkDiscovery(ILogger<HiLinkDiscovery> log)
    {
        _log = log;
    }

    public static string[] DiscoverGatewayIps()
    {
        var gateways = new List<string>();

        foreach (var ip in DefaultHiLinkIps)
            gateways.Add(ip);

        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var props = iface.GetIPProperties();
                foreach (var gw in props.GatewayAddresses)
                {
                    var addr = gw.Address;
                    if (IPAddress.IsLoopback(addr)) continue;
                    if (addr.AddressFamily != AddressFamily.InterNetwork) continue;

                    var ip = addr.ToString();
                    if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0" && seen.Add(ip))
                        gateways.Add(ip);
                }
            }
        }
        catch { }

        return gateways.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<List<HiLinkDeviceInfo>> DiscoverAsync(string[] ips, int timeoutMs = 2000)
    {
        var results = new ConcurrentBag<HiLinkDeviceInfo>();
        var sw = Stopwatch.StartNew();

        _log.LogInformation("Probing {Count} IPs (parallel, {Ms}ms timeout)...", ips.Length, timeoutMs);

        var tasks = ips.Select(ip => ProbeIpAsync(ip, timeoutMs, results));
        await Task.WhenAll(tasks);

        sw.Stop();
        _log.LogInformation("Probe complete: {Count} device(s) found in {Ms}ms", results.Count, sw.ElapsedMilliseconds);
        return results.ToList();
    }

    private async Task ProbeIpAsync(string ip, int timeoutMs, ConcurrentBag<HiLinkDeviceInfo> results)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
                var response = await http.GetAsync($"http://{ip}/api/webserver/SesTokInfo");
                var xml = await response.Content.ReadAsStringAsync();
                var doc = XDocument.Parse(xml);
                var root = doc.Root;
                if (root == null) return;

                var sesInfo = root.Element(Ns + "SesInfo")?.Value;
                if (string.IsNullOrEmpty(sesInfo)) return;

                var info = new HiLinkDeviceInfo { Ip = ip, SessionCookie = sesInfo };

                var tokInfo = root.Element(Ns + "TokInfo")?.Value;
                if (!string.IsNullOrEmpty(tokInfo))
                    info.CsrfToken = tokInfo;

                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"http://{ip}/api/device/information");
                    request.Headers.Add("Cookie", $"SessionID={sesInfo}");
                    var devResp = await http.SendAsync(request);
                    var devXml = await devResp.Content.ReadAsStringAsync();
                    var devDoc = XDocument.Parse(devXml);
                    var devRoot = devDoc.Root;
                    if (devRoot != null)
                    {
                        info.Imei = devRoot.Element(Ns + "Imei")?.Value ?? "";
                        info.Imsi = devRoot.Element(Ns + "Imsi")?.Value ?? "";
                        info.Model = devRoot.Element(Ns + "DeviceName")?.Value ?? "";
                        info.Manufacturer = devRoot.Element(Ns + "Manufacturer")?.Value ?? "Huawei";
                    }
                }
                catch { }

                _log.LogInformation("{Ip}: FOUND HiLink | IMEI={IMEI} Model={Model}",
                    ip, info.Imei, info.Model);
                results.Add(info);
                return;
            }
            catch (HttpRequestException) when (attempt < 2) { await Task.Delay(300); }
            catch (TaskCanceledException) when (attempt < 2) { await Task.Delay(300); }
            catch (HttpRequestException) { return; }
            catch (TaskCanceledException) { return; }
            catch { return; }
        }
    }
}

public class HiLinkDeviceInfo
{
    public string Ip { get; set; } = "";
    public string Imei { get; set; } = "";
    public string Imsi { get; set; } = "";
    public string Model { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string? SessionCookie { get; set; }
    public string? CsrfToken { get; set; }
}
