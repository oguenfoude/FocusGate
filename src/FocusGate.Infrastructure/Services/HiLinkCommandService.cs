using System.Net.Http;
using System.Net.Security;
using System.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FocusGate.Core.DTOs;
using FocusGate.Core.Enums;
using FocusGate.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

public partial class HiLinkCommandService : IAtCommandService
{
    private readonly ILogger<HiLinkCommandService> _log;
    private readonly IConfigProvider _config;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _baseUrl;
    private string? _sessionCookie;
    private string? _csrfToken;
    private bool _isOpen;
    private bool _disposed;
    private string? _imei;
    private string? _imsi;

    public bool IsOpen => _isOpen;
    public string? ComPort => null;

    private static readonly XNamespace Ns = "http://schemas.datacontract.org/2004/07/Huawei.Hilink.DataModel";

    private static string? GetElement(XElement parent, string name)
    {
        return parent.Element(Ns + name)?.Value ?? parent.Element(name)?.Value;
    }

    private static IEnumerable<XElement> GetElements(XElement parent, string name)
    {
        return parent.Elements(Ns + name).Concat(parent.Elements(name));
    }

    public HiLinkCommandService(ILogger<HiLinkCommandService> log, IConfigProvider config)
    {
        _log = log;
        _config = config;
        _http = new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task OpenAsync(string ip)
    {
        ip = ip.Trim();
        _baseUrl = $"http://{ip}";

        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/api/webserver/SesTokInfo");
            var xml = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);
            var root = doc.Root!;

            var sesInfo = GetElement(root, "SesInfo");
            var tokInfo = GetElement(root, "TokInfo");

            if (!string.IsNullOrEmpty(sesInfo))
            {
                _sessionCookie = sesInfo;
            }

            if (!string.IsNullOrEmpty(tokInfo))
            {
                _csrfToken = tokInfo;
            }

            _isOpen = true;
            _log.LogInformation("HiLink {Ip}: Connected (HTTP)", ip);

            await RefreshCsrfFromGetAsync("/api/device/information");
        }
        catch (HttpRequestException)
        {
            try
            {
                _log.LogDebug("HiLink {Ip}: HTTP failed, trying HTTPS...", ip);
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                };
                using var https = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                _baseUrl = $"https://{ip}";
                var response = await https.GetAsync($"{_baseUrl}/api/webserver/SesTokInfo");
                var xml = await response.Content.ReadAsStringAsync();
                var doc = XDocument.Parse(xml);
                var root = doc.Root!;

                var sesInfo = GetElement(root, "SesInfo");
                var tokInfo = GetElement(root, "TokInfo");

                if (!string.IsNullOrEmpty(sesInfo))
                {
                    _sessionCookie = sesInfo;
                }

                if (!string.IsNullOrEmpty(tokInfo))
                {
                    _csrfToken = tokInfo;
                }

                _isOpen = true;
                _log.LogInformation("HiLink {Ip}: Connected (HTTPS)", ip);

                await RefreshCsrfFromGetAsync("/api/device/information");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HiLink {Ip}: Connection failed on both HTTP and HTTPS", ip);
                throw new InvalidOperationException($"HiLink connection failed at {ip}: {ex.Message}");
            }
        }
    }

    private async Task RefreshCsrfFromGetAsync(string path)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}");
            ApplyHeaders(request);
            var response = await _http.SendAsync(request);
            UpdateCsrfFromResponse(response);
            _ = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[HiLink] CSRF refresh GET failed (non-critical)");
        }
    }

    public async Task<bool> IsAliveAsync()
    {
        if (string.IsNullOrEmpty(_baseUrl)) return false;
        try
        {
            var response = await SendGetAsync("/api/monitoring/status");
            return !string.IsNullOrEmpty(response);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TryRefreshSessionAsync()
    {
        if (string.IsNullOrEmpty(_baseUrl)) return false;
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/api/webserver/SesTokInfo");
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("[HiLink] SesTokInfo returned {StatusCode}", response.StatusCode);
                return false;
            }
            var xml = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);
            var root = doc.Root!;

            var sesInfo = GetElement(root, "SesInfo");
            var tokInfo = GetElement(root, "TokInfo");

            if (string.IsNullOrEmpty(sesInfo))
            {
                _log.LogWarning("[HiLink] Session refresh: no SesInfo in response");
                return false;
            }

            _sessionCookie = sesInfo;
            if (!string.IsNullOrEmpty(tokInfo))
                _csrfToken = tokInfo;

            _isOpen = true;

            _log.LogInformation("[HiLink] Session refreshed successfully");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[HiLink] Session refresh failed");
            return false;
        }
    }

    private async Task<bool> TryLoginAsync()
    {
        var user = _config.Get("hilink.username", "admin");
        var pass = _config.Get("hilink.password", "admin");

        var body = $@"<request><Username>{SecurityElement.Escape(user)}</Username><Password>{SecurityElement.Escape(pass)}</Password></request>";

        try
        {
            var xml = await SendPostAsync("/api/user/login", body);
            if (string.IsNullOrEmpty(xml)) return false;

            return xml.Contains("OK", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[HiLink] Login attempt failed (non-critical)");
            return false;
        }
    }

    public async Task<string> GetImeiAsync()
    {
        if (!string.IsNullOrEmpty(_imei)) return _imei;

            var xml = await SendGetAsync("/api/device/information");
            if (!string.IsNullOrEmpty(xml))
            {
                var doc = XDocument.Parse(xml);
            _imei = GetElement(doc.Root!, "Imei") ?? GetElement(doc.Root!, "imei") ?? string.Empty;
            _imsi = GetElement(doc.Root!, "Imsi") ?? GetElement(doc.Root!, "imsi") ?? string.Empty;
        }

        if (string.IsNullOrEmpty(_imei))
        {
            _log.LogWarning("[HiLink] IMEI not available from /api/device/information — modem will be skipped");
        }

        return _imei ?? string.Empty;
    }

    public async Task<string> GetImsiAsync()
    {
        if (!string.IsNullOrEmpty(_imsi)) return _imsi;
        await GetImeiAsync();

        if (string.IsNullOrEmpty(_imsi))
        {
            _log.LogWarning("[HiLink] IMSI not available from /api/device/information — modem may not register");
        }

        return _imsi ?? string.Empty;
    }

    public async Task<NetworkRegistration> GetNetworkRegistrationAsync()
    {
        var xml = await SendGetAsync("/api/monitoring/status");
        if (string.IsNullOrEmpty(xml)) return NetworkRegistration.Unknown;

        var doc = XDocument.Parse(xml);

        var serviceStatus = GetElement(doc.Root!, "ServiceStatus");
        if (int.TryParse(serviceStatus, out var svc) && svc == 2)
            return NetworkRegistration.Registered;

        var connStatus = GetElement(doc.Root!, "ConnectionStatus");
        if (int.TryParse(connStatus, out var val))
        {
            return val switch
            {
                2 => NetworkRegistration.Registered,
                0 or 1 => NetworkRegistration.NotRegistered,
                3 => NetworkRegistration.Denied,
                _ => NetworkRegistration.Unknown
            };
        }
        return NetworkRegistration.Unknown;
    }

    public async Task<string> GetPhoneNumberViaUssdAsync()
    {
        var phoneCode = _config.Get("modem.ussd.phone_code", "*101#");
        var resp = await SendUssdAsync(phoneCode, 15000);

        if (string.IsNullOrEmpty(resp)) return string.Empty;

        var phoneMatch = Regex.Match(resp, @"(\d{10,12})");
        return phoneMatch.Success ? phoneMatch.Value : string.Empty;
    }

    public Task<string> GetPhoneNumberViaCnumAsync() => Task.FromResult(string.Empty);

    public async Task<decimal?> GetBalanceAsync()
    {
        var balanceCode = _config.Get("modem.ussd.balance_code", "*222#");
        var resp = await SendUssdAsync(balanceCode, 15000);

        if (string.IsNullOrEmpty(resp)) return null;

        if (!resp.Contains("Solde") && !resp.Contains("solde") && !resp.Contains("DA") && !resp.Contains("DZD"))
        {
            _log.LogWarning("[HiLink] Response does not contain balance: {Resp}", resp);
            return null;
        }

        var match = BalanceRegex().Match(resp);
        if (match.Success)
        {
            var numStr = match.Groups[1].Value;
            var parsed = ParseFrenchNumber(numStr);
            if (parsed.HasValue)
                return parsed.Value;
        }
        return null;
    }

    private static decimal? ParseFrenchNumber(string numStr)
    {
        numStr = numStr.TrimEnd('.', ',');
        if (numStr.Contains(',') && numStr.Contains('.'))
        {
            var lastComma = numStr.LastIndexOf(',');
            var lastDot = numStr.LastIndexOf('.');
            if (lastComma > lastDot)
            {
                numStr = numStr.Replace(".", "").Replace(",", ".");
            }
            else
            {
                numStr = numStr.Replace(",", "");
            }
        }
        else if (numStr.Contains(','))
        {
            numStr = numStr.Replace(",", ".");
        }

        if (decimal.TryParse(numStr, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount))
            return amount;
        return null;
    }

    public async Task<List<RawSmsMessage>> ReadAllSmsAsync()
    {
        var messages = new List<RawSmsMessage>();
        try
        {
            var body = @"<request><PageIndex>1</PageIndex><ReadCount>50</ReadCount><BoxType>1</BoxType><SortType>0</SortType><Ascending>0</Ascending><UnreadPreferred>0</UnreadPreferred></request>";
            var xml = await SendPostAsync("/api/sms/sms-list", body);
            if (string.IsNullOrEmpty(xml)) return messages;

            var doc = XDocument.Parse(xml);

            var errorEl = GetElement(doc.Root!, "code");
            if (errorEl != null && doc.Root!.Name.LocalName == "error")
            {
                _log.LogWarning("[HiLink] SMS list returned error {Code}: {Xml}", errorEl, xml.Length > 200 ? xml[..200] : xml);
                return messages;
            }

            var msgElements = GetElements(doc.Root!, "Messages").Elements().ToList();
            var messageElements = msgElements.Count > 0 ? msgElements : GetElements(doc.Root!, "Message").ToList();
            if (!messageElements.Any()) return messages;

            foreach (var el in messageElements)
            {
                var phone = GetElement(el, "Phone") ?? "";
                var content = GetElement(el, "Content") ?? "";
                var dateStr = GetElement(el, "Date") ?? "";
                var indexStr = GetElement(el, "Index") ?? "0";

                if (!int.TryParse(indexStr, out var idx)) idx = 0;
                if (!DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                    dt = DateTime.UtcNow;

                messages.Add(new RawSmsMessage
                {
                    Index = idx,
                    Status = "REC READ",
                    Sender = phone,
                    ReceivedAt = dt,
                    Content = content
                });
            }

        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[HiLink] ReadAllSms failed");
        }
        return messages;
    }

    public async Task DeleteAllSmsAsync()
    {
        try
        {
            var messages = await ReadAllSmsAsync();
            foreach (var msg in messages)
            {
                var body = $@"<request><Index>{msg.Index}</Index></request>";
                await SendPostAsync("/api/sms/delete-sms", body);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[HiLink] DeleteAllSms failed");
        }
    }

    public async Task<string> SendUssdAsync(string code, int timeoutMs = 15000)
    {
        await _lock.WaitAsync();
        try
        {
            // Step 0: Release any existing USSD dialog (no cookies, no CSRF — browser pattern)
            await SendUssdRawGetAsync("/api/ussd/release");

            // Step 1: Send USSD code via form-urlencoded
            var escaped = System.Security.SecurityElement.Escape(code);
            var body = $@"<?xml version=""1.0"" encoding=""UTF-8""?><request><content>{escaped}</content><codeType>CodeType</codeType><timeout></timeout></request>";
            var xml = await SendUssdSendAsync("/api/ussd/send", body);
            if (string.IsNullOrEmpty(xml))
            {
                _log.LogWarning("[HiLink] USSD {Code}: empty response from /api/ussd/send", code);
                return string.Empty;
            }

            var sendDoc = XDocument.Parse(xml);
            var sendError = GetElement(sendDoc.Root!, "code");
            if (sendError != null && sendDoc.Root!.Name.LocalName == "error")
            {
                _log.LogWarning("[HiLink] USSD {Code} send error {Error}: {Xml}",
                    code, sendError, xml.Length > 200 ? xml[..200] : xml);
                return string.Empty;
            }

            // Step 2: Poll /api/ussd/get — no CSRF (browser pattern)
            var pollIntervalMs = 2000;
            var totalTimeoutMs = 30_000;
            var deadline = DateTime.UtcNow.AddMilliseconds(totalTimeoutMs);
            var pollCount = 0;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(pollIntervalMs);
                pollCount++;
                var getResult = await SendUssdRawGetAsync("/api/ussd/get");
                if (string.IsNullOrEmpty(getResult))
                {
                    continue;
                }

                var getDoc = XDocument.Parse(getResult);
                var rootName = getDoc.Root!.Name.LocalName;
                if (rootName == "error")
                {
                    var getError = GetElement(getDoc.Root!, "code");
                    if (getError == "111019")
                    {
                        continue;
                    }

                    _log.LogWarning("[HiLink] USSD {Code} poll error {Error}",
                        code, getError);
                    break;
                }

                var content = GetElement(getDoc.Root!, "Content")
                    ?? GetElement(getDoc.Root!, "content");
                if (!string.IsNullOrEmpty(content))
                {
                    await SendUssdRawGetAsync("/api/ussd/release");
                    return content;
                }

                _log.LogWarning("[HiLink] USSD {Code} poll #{Count}: unrecognized XML: {Xml}",
                    code, pollCount, getResult.Length > 300 ? getResult[..300] : getResult);
            }

            _log.LogWarning("[HiLink] USSD {Code}: no response after {Count} polls ({Seconds}s)",
                code, pollCount, (int)(pollCount * pollIntervalMs / 1000));
            await SendUssdRawGetAsync("/api/ussd/release");
            return string.Empty;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string?> SendUssdSendAsync(string path, string xmlBody)
    {
        if (string.IsNullOrEmpty(_baseUrl)) return null;

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}")
        {
            Content = new StringContent(xmlBody, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        ApplyHeaders(request);
        request.Headers.Add("Referer", $"{_baseUrl}/html/ussd.html");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("Origin", _baseUrl);
        if (!string.IsNullOrEmpty(_csrfToken))
            request.Headers.Add("__RequestVerificationToken", _csrfToken);

        var response = await _http.SendAsync(request);
        UpdateCsrfFromResponse(response);
        var respBody = await response.Content.ReadAsStringAsync();
        _log.LogDebug("[HiLink] USSD POST {Path} → {Status}", path, response.StatusCode);
        response.EnsureSuccessStatusCode();
        return respBody;
    }

    private async Task<string?> SendUssdRawGetAsync(string path)
    {
        if (string.IsNullOrEmpty(_baseUrl)) return null;

        var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}");
        if (!string.IsNullOrEmpty(_sessionCookie))
        {
            var cv = _sessionCookie.StartsWith("SessionID=", StringComparison.OrdinalIgnoreCase)
                ? _sessionCookie["SessionID=".Length..]
                : _sessionCookie;
            request.Headers.Add("Cookie", $"SessionID={cv}");
        }
        request.Headers.Add("Referer", $"{_baseUrl}/html/ussd.html");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public Task<bool> TrySetCharsetAsync(string charset) => Task.FromResult(true);

    public async Task<string> SendCommandAsync(string command, int timeoutMs = 5000)
    {
        return "OK";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isOpen = false;
        _http.Dispose();
    }

    private async Task<string?> SendGetAsync(string path)
    {
        if (string.IsNullOrEmpty(_baseUrl)) return null;

        var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}");
        ApplyHeaders(request);

        var response = await _http.SendAsync(request);
        UpdateCsrfFromResponse(response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string?> SendPostAsync(string path, string xmlBody)
    {
        if (string.IsNullOrEmpty(_baseUrl)) return null;

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}")
        {
            Content = new StringContent(xmlBody, Encoding.UTF8, "application/xml")
        };
        ApplyHeaders(request);

        var response = await _http.SendAsync(request);
        UpdateCsrfFromResponse(response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private void UpdateCsrfFromResponse(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("__RequestVerificationToken", out var values))
        {
            var newToken = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(newToken) && newToken != _csrfToken)
            {
                _csrfToken = newToken;
            }
        }
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_sessionCookie))
        {
            var cookieValue = _sessionCookie.StartsWith("SessionID=", StringComparison.OrdinalIgnoreCase)
                ? _sessionCookie["SessionID=".Length..]
                : _sessionCookie;
            request.Headers.Add("Cookie", $"SessionID={cookieValue}");
        }

        if (!string.IsNullOrEmpty(_csrfToken))
            request.Headers.Add("__RequestVerificationToken", _csrfToken);

        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
    }

    [GeneratedRegex(@"(\d[\d.,]+)")]
    private static partial Regex BalanceRegex();
}
