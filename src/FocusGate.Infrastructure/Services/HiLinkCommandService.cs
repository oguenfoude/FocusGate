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
    public bool IsSmsInboxFull { get; private set; }
    public bool LastRequestFailed { get; private set; }
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

        try
        {
            _baseUrl = $"http://{ip}";
            await ConnectAsync(_baseUrl, ip, isHttps: false);
            await RefreshCsrfFromGetAsync("/api/device/information");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException)
        {
            _log.LogDebug("HiLink {Ip}: HTTP failed ({Error}), trying HTTPS...", ip, ex.GetType().Name);
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };
            using var https = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            _baseUrl = $"https://{ip}";
            await ConnectAsync(_baseUrl, ip, isHttps: true, client: https);
            await RefreshCsrfFromGetAsync("/api/device/information", client: https);
        }
    }

    private async Task ConnectAsync(string baseUrl, string ip, bool isHttps, HttpClient? client = null)
    {
        var http = client ?? _http;
        var response = await http.GetAsync($"{baseUrl}/api/webserver/SesTokInfo");
        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        var root = doc.Root!;

        var sesInfo = GetElement(root, "SesInfo");
        var tokInfo = GetElement(root, "TokInfo");

        if (!string.IsNullOrEmpty(sesInfo))
            _sessionCookie = sesInfo;

        if (!string.IsNullOrEmpty(tokInfo))
            _csrfToken = tokInfo;

        if (string.IsNullOrEmpty(_sessionCookie))
        {
            _log.LogWarning("HiLink {Ip}: {Scheme} connected but no SesInfo — session may not work", ip, isHttps ? "HTTPS" : "HTTP");
        }

        _isOpen = true;
        _log.LogInformation("HiLink {Ip}: Connected ({Scheme})", ip, isHttps ? "HTTPS" : "HTTP");
    }

    private async Task RefreshCsrfFromGetAsync(string path, HttpClient? client = null)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}");
            ApplyHeaders(request);
            var http = client ?? _http;
            var response = await http.SendAsync(request);
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
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[HiLink] IsAlive check failed");
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
                _sessionCookie = null;
                _csrfToken = null;
                _isOpen = false;
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
                _sessionCookie = null;
                _csrfToken = null;
                _isOpen = false;
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
            _sessionCookie = null;
            _csrfToken = null;
            _isOpen = false;
            return false;
        }
    }

    public async Task<string> GetImeiAsync()
    {
        if (!string.IsNullOrEmpty(_imei)) return _imei;

        try
        {
            var xml = await SendGetAsync("/api/device/information");
            if (!string.IsNullOrEmpty(xml))
            {
                var doc = XDocument.Parse(xml);
                _imei = GetElement(doc.Root!, "Imei") ?? GetElement(doc.Root!, "imei") ?? string.Empty;
                _imsi = GetElement(doc.Root!, "Imsi") ?? GetElement(doc.Root!, "imsi") ?? string.Empty;
            }
        }
        catch (System.Xml.XmlException ex)
        {
            _log.LogWarning(ex, "[HiLink] IMEI parse failed — modem may return non-XML");
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
        string? xml;
        try
        {
            xml = await SendGetAsync("/api/monitoring/status");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[HiLink] Network registration GET failed");
            return NetworkRegistration.Unknown;
        }
        if (string.IsNullOrEmpty(xml)) return NetworkRegistration.Unknown;

        try
        {
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
        }
        catch (System.Xml.XmlException ex)
        {
            _log.LogWarning(ex, "[HiLink] Network registration parse failed (xml={Xml})",
                xml.Length > 200 ? xml[..200] : xml);
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
            {
                _log.LogInformation("[HiLink] Balance: {Balance:F2} DZD", parsed.Value);
                return parsed.Value;
            }
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
        string? xml;
        try
        {
            var body = @"<request><PageIndex>1</PageIndex><ReadCount>50</ReadCount><BoxType>1</BoxType><SortType>0</SortType><Ascending>0</Ascending><UnreadPreferred>0</UnreadPreferred></request>";
            xml = await SendPostAsync("/api/sms/sms-list", body);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[HiLink] ReadAllSms HTTP request failed");
            throw;
        }
        if (string.IsNullOrEmpty(xml)) return messages;

        try
        {
            var doc = XDocument.Parse(xml);

            var errorEl = GetElement(doc.Root!, "code");
            if (errorEl != null && doc.Root!.Name.LocalName == "error")
            {
                if (errorEl == "125002")
                    IsSmsInboxFull = true;
                _log.LogWarning("[HiLink] SMS list returned error {Code}: {Xml}", errorEl, xml.Length > 200 ? xml[..200] : xml);
                return messages;
            }

            IsSmsInboxFull = false;

            var msgElements = GetElements(doc.Root!, "Messages").Elements().ToList();
            var messageElements = msgElements.Count > 0 ? msgElements : GetElements(doc.Root!, "Message").ToList();
            if (!messageElements.Any()) return messages;

            foreach (var el in messageElements)
            {
                var phone = (GetElement(el, "Phone") ?? "").Trim();
                var content = GetElement(el, "Content") ?? "";
                var dateStr = GetElement(el, "Date") ?? "";
                var indexStr = GetElement(el, "Index") ?? "0";

                if (!int.TryParse(indexStr, out var idx)) idx = 0;

                // HiLink modems return "dd/MM/yyyy, HH:mm" — InvariantCulture expects MM/dd/yyyy and fails.
                // Parse manually, then convert from Algeria local to UTC.
                var dt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(dateStr))
                {
                    var clean = dateStr.Replace(",", "").Trim();
                    string[] formats = ["dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-dd HH:mm:ss"];
                    if (DateTime.TryParseExact(clean, formats, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                    {
                        var unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
                        try
                        {
                            var alTz = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers") ?? TimeZoneInfo.Utc;
                            dt = TimeZoneInfo.ConvertTimeToUtc(unspecified, alTz);
                        }
                        catch
                        {
                            dt = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                        }
                    }
                    else
                    {
                        dt = DateTime.UtcNow;
                    }
                }
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

                var contentTime = ExtractTimestampFromContent(content);
                if (contentTime.HasValue)
                {
                    _log.LogDebug("[HiLink] Content timestamp override: SCTS={Scts} → Content={Content} (UTC={Utc})", dt, contentTime.Value.AddHours(1), contentTime.Value);
                    dt = contentTime.Value;
                }

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
            _log.LogWarning(ex, "[HiLink] ReadAllSms parse failed (xml={Xml})", xml.Length > 200 ? xml[..200] : xml);
        }
        return messages;
    }

    public async Task DeleteAllSmsAsync()
    {
        try
        {
            var messages = await ReadAllSmsAsync();

            if (messages.Count > 0)
            {
                var deleteFailed = 0;
                foreach (var msg in messages)
                {
                    try
                    {
                        var body = $@"<request><Index>{msg.Index}</Index></request>";
                        await SendPostAsync("/api/sms/delete-sms", body);
                    }
                    catch (Exception ex)
                    {
                        deleteFailed++;
                        _log.LogWarning(ex, "[HiLink] Delete SMS index {Index} failed", msg.Index);
                    }
                }
                if (deleteFailed > 0)
                    _log.LogWarning("[HiLink] DeleteAllSms: {Failed}/{Total} deletes failed", deleteFailed, messages.Count);
                return;
            }

            if (IsSmsInboxFull)
            {
                _log.LogWarning("[HiLink] ReadAllSms returned empty on 125002 — deleting by index fallback (1–100)");
                for (int i = 1; i <= 100; i++)
                {
                    try
                    {
                        var body = $@"<request><Index>{i}</Index></request>";
                        await SendPostAsync("/api/sms/delete-sms", body);
                    }
                    catch (HttpRequestException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[HiLink] Index delete failed at {Index} — aborting fallback", i);
                        break;
                    }
                }
                IsSmsInboxFull = false;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[HiLink] DeleteAllSms failed");
        }
    }

    public async Task<string> SendUssdAsync(string code, int timeoutMs = 15000)
    {
        if (!await _lock.WaitAsync(TimeSpan.FromSeconds(15)))
        {
            _log.LogWarning("[HiLink] USSD {Code}: lock acquisition timed out", code);
            return string.Empty;
        }
        try
        {
            _log.LogInformation("[HiLink] USSD {Code}: sending...", code);
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

            XDocument? sendDoc = null;
            try
            {
                sendDoc = XDocument.Parse(xml);
            }
            catch (System.Xml.XmlException ex)
            {
                _log.LogWarning(ex, "[HiLink] USSD {Code}: send response parse failed (xml={Xml})",
                    code, xml.Length > 200 ? xml[..200] : xml);
                await SendUssdRawGetAsync("/api/ussd/release");
                return string.Empty;
            }

            var sendError = GetElement(sendDoc.Root!, "code");
            if (sendError != null && sendDoc.Root!.Name.LocalName == "error")
            {
                _log.LogWarning("[HiLink] USSD {Code} send error {Error}: {Xml}",
                    code, sendError, xml.Length > 200 ? xml[..200] : xml);
                return string.Empty;
            }

            // Step 2: Poll /api/ussd/get — no CSRF (browser pattern)
            var pollIntervalMs = 2000;
            var totalTimeoutMs = timeoutMs;
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

                XDocument? getDoc = null;
                try
                {
                    getDoc = XDocument.Parse(getResult);
                }
                catch (System.Xml.XmlException ex)
                {
                    _log.LogWarning(ex, "[HiLink] USSD {Code} poll #{Count}: parse failed (xml={Xml})",
                        code, pollCount, getResult.Length > 300 ? getResult[..300] : getResult);
                    continue;
                }

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

    public Task<string> SendCommandAsync(string command, int timeoutMs = 5000)
    {
        throw new NotSupportedException("AT commands are not supported on HiLink modems (use HTTP API methods instead).");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isOpen = false;
        try { _http.Dispose(); } catch { }
        try { _lock.Dispose(); } catch { }
    }

    private async Task<string?> SendGetAsync(string path)
    {
        if (string.IsNullOrEmpty(_baseUrl)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}");
        ApplyHeaders(request);

        var response = await _http.SendAsync(request);
        UpdateCsrfFromResponse(response);
        if (!response.IsSuccessStatusCode)
        {
            LastRequestFailed = true;
            _log.LogWarning("[HiLink] GET {Path} returned {StatusCode}", path, response.StatusCode);
            throw new HttpRequestException($"GET {path} returned {response.StatusCode}");
        }
        LastRequestFailed = false;
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string?> SendPostAsync(string path, string xmlBody)
    {
        if (string.IsNullOrEmpty(_baseUrl)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}")
        {
            Content = new StringContent(xmlBody, Encoding.UTF8, "application/xml")
        };
        ApplyHeaders(request);

        var response = await _http.SendAsync(request);
        UpdateCsrfFromResponse(response);
        if (!response.IsSuccessStatusCode)
        {
            LastRequestFailed = true;
            _log.LogWarning("[HiLink] POST {Path} returned {StatusCode}", path, response.StatusCode);
            throw new HttpRequestException($"POST {path} returned {response.StatusCode}");
        }
        LastRequestFailed = false;
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

    internal static DateTime? ExtractTimestampFromContent(string content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var match = ContentTimestampRegex().Match(content);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var dd)) return null;
        if (!int.TryParse(match.Groups[2].Value, out var mm)) return null;
        if (!int.TryParse(match.Groups[3].Value, out var yyyy)) return null;
        if (!int.TryParse(match.Groups[4].Value, out var hh)) return null;
        if (!int.TryParse(match.Groups[5].Value, out var mnn)) return null;
        if (!int.TryParse(match.Groups[6].Value, out var ss)) return null;
        if (yyyy < 2000 || yyyy > 2099 || mm < 1 || mm > 12 || dd < 1 || dd > 31) return null;
        if (hh > 23 || mnn > 59 || ss > 59) return null;
        var local = new DateTime(yyyy, mm, dd, hh, mnn, ss, DateTimeKind.Unspecified);
        var algeriaTz = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, algeriaTz);
        return utc;
    }

    [GeneratedRegex(@"le\s+(\d{1,2})/(\d{1,2})/(\d{4})\s+(\d{1,2}):(\d{2}):(\d{2})")]
    private static partial Regex ContentTimestampRegex();

    [GeneratedRegex(@"(\d[\d.,]+)")]
    private static partial Regex BalanceRegex();
}
