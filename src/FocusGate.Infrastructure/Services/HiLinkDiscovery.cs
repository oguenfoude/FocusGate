using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

public class HiLinkDiscovery
{
    private readonly ILogger<HiLinkDiscovery> _log;
    private static readonly XNamespace Ns = "http://schemas.datacontract.org/2004/07/Huawei.Hilink.DataModel";

    public HiLinkDiscovery(ILogger<HiLinkDiscovery> log)
    {
        _log = log;
    }

    public async Task<List<HiLinkDeviceInfo>> DiscoverAsync(string[] ips, int timeoutMs = 3000)
    {
        var found = new List<HiLinkDeviceInfo>();
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

        foreach (var ip in ips)
        {
            try
            {
                var response = await http.GetAsync($"http://{ip}/api/webserver/SesTokInfo");
                var xml = await response.Content.ReadAsStringAsync();
                var doc = XDocument.Parse(xml);
                var root = doc.Root;

                if (root == null) continue;

                var sesInfo = root.Element(Ns + "SesInfo")?.Value;
                if (string.IsNullOrEmpty(sesInfo)) continue;

                _log.LogInformation("{Ip}: HiLink modem detected", ip);

                var info = new HiLinkDeviceInfo { Ip = ip, SessionCookie = sesInfo };

                var tokInfo = root.Element(Ns + "TokInfo")?.Value;
                if (!string.IsNullOrEmpty(tokInfo))
                    info.CsrfToken = tokInfo;

                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"http://{ip}/api/device/information");
                    if (!string.IsNullOrEmpty(sesInfo))
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
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "{Ip}: Failed to get device info", ip);
                }

                found.Add(info);
                _log.LogInformation("{Ip}: IMEI={IMEI} Model={Model}", ip, info.Imei, info.Model);
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "{Ip}: Not a HiLink modem", ip);
            }
        }

        return found;
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
