using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

public class HiLinkDiscovery
{
    private readonly ILogger<HiLinkDiscovery> _log;
    private static readonly XNamespace Ns = "http://schemas.datacontract.org/2004/07/Huawei.Hilink.DataModel";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

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
            var useSsl = attempt == 2;
            var scheme = useSsl ? "https" : "http";
            var url = $"{scheme}://{ip}/api/webserver/SesTokInfo";

            try
            {
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                };
                using var http = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromMilliseconds(timeoutMs)
                };
                http.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                var response = await http.GetAsync(url);
                var xml = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(xml))
                {
                    _log.LogWarning("{Ip}: Empty response ({Scheme})", ip, scheme);
                    continue;
                }

                if (xml.TrimStart().StartsWith("<!DOCTYPE") || xml.TrimStart().StartsWith("<html"))
                {
                    _log.LogWarning("{Ip}: Got HTML login page ({Scheme}) — not HiLink API", ip, scheme);
                    continue;
                }

                XDocument doc;
                try
                {
                    doc = XDocument.Parse(xml);
                }
                catch
                {
                    _log.LogWarning("{Ip}: Invalid XML ({Scheme}, first 80 chars: {Preview})", ip, scheme, xml.Length > 80 ? xml[..80] : xml);
                    continue;
                }

                var root = doc.Root;
                if (root == null)
                {
                    _log.LogWarning("{Ip}: Empty XML root ({Scheme})", ip, scheme);
                    continue;
                }

                var sesInfo = root.Element(Ns + "SesInfo")?.Value
                    ?? root.Element("SesInfo")?.Value;
                if (string.IsNullOrEmpty(sesInfo))
                {
                    _log.LogWarning("{Ip}: Got XML but no SesInfo (root element: {Root}) — not a standard HiLink modem", ip, root.Name.LocalName);
                    continue;
                }

                var info = new HiLinkDeviceInfo { Ip = ip, SessionCookie = sesInfo };

                var tokInfo = root.Element(Ns + "TokInfo")?.Value
                    ?? root.Element("TokInfo")?.Value;
                if (!string.IsNullOrEmpty(tokInfo))
                    info.CsrfToken = tokInfo;

                try
                {
                    var sesCookie = sesInfo.StartsWith("SessionID=", StringComparison.OrdinalIgnoreCase)
                        ? sesInfo["SessionID=".Length..]
                        : sesInfo;
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{scheme}://{ip}/api/device/information");
                    request.Headers.Add("Cookie", $"SessionID={sesCookie}");
                    request.Headers.Add("User-Agent", UserAgent);
                    request.Headers.Add("X-Requested-With", "XMLHttpRequest");
                    var devResp = await http.SendAsync(request);
                    var devXml = await devResp.Content.ReadAsStringAsync();
                    var devDoc = XDocument.Parse(devXml);
                    var devRoot = devDoc.Root;
                    if (devRoot != null)
                    {
                        info.Imei = devRoot.Element(Ns + "Imei")?.Value
                            ?? devRoot.Element("Imei")?.Value
                            ?? devRoot.Element(Ns + "imei")?.Value
                            ?? devRoot.Element("imei")?.Value ?? "";
                        info.Imsi = devRoot.Element(Ns + "Imsi")?.Value
                            ?? devRoot.Element("Imsi")?.Value
                            ?? devRoot.Element(Ns + "imsi")?.Value
                            ?? devRoot.Element("imsi")?.Value ?? "";
                        info.Model = devRoot.Element(Ns + "DeviceName")?.Value
                            ?? devRoot.Element("DeviceName")?.Value
                            ?? devRoot.Element(Ns + "devicename")?.Value
                            ?? devRoot.Element("devicename")?.Value ?? "";
                        info.Manufacturer = devRoot.Element(Ns + "Manufacturer")?.Value
                            ?? devRoot.Element("Manufacturer")?.Value ?? "Huawei";

                if (string.IsNullOrEmpty(info.Imei))
                {
                    _log.LogWarning("{Ip}: IMEI not returned by device info — skipping (HiLink modems must provide IMEI via API)", ip);
                    return;
                }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "{Ip}: Failed to get device info (non-critical)", ip);
                }

                if (string.IsNullOrEmpty(info.Imei))
                {
                    _log.LogWarning("{Ip}: IMEI empty after device info — skipping", ip);
                    return;
                }

                _log.LogInformation("{Ip}: FOUND HiLink ({Scheme}) | IMEI={IMEI} Model={Model}",
                    ip, scheme.ToUpperInvariant(), info.Imei, info.Model);
                results.Add(info);
                return;
            }
            catch (HttpRequestException ex) when (attempt == 1)
            {
                _log.LogWarning("{Ip}: Connection failed ({Scheme}: {Error}), will try HTTPS", ip, scheme, ex.Message);
                await Task.Delay(100);
            }
            catch (HttpRequestException ex)
            {
                _log.LogWarning("{Ip}: Connection failed on both HTTP and HTTPS ({Error})", ip, ex.Message);
                return;
            }
            catch (TaskCanceledException) when (attempt == 1)
            {
                _log.LogWarning("{Ip}: Timeout ({Scheme}), will try HTTPS", ip, scheme);
                await Task.Delay(100);
            }
            catch (TaskCanceledException)
            {
                _log.LogWarning("{Ip}: Timeout on both HTTP and HTTPS", ip);
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "{Ip}: Unexpected error ({Scheme})", ip, scheme);
                return;
            }
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
