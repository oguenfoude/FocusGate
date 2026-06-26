using System.Net.Http;
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

    public HiLinkCommandService(ILogger<HiLinkCommandService> log, IConfigProvider config)
    {
        _log = log;
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task OpenAsync(string ip)
    {
        _baseUrl = $"http://{ip.Trim()}";

        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/api/webserver/SesTokInfo");
            var xml = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);
            var root = doc.Root!;

            var sesInfo = root.Element(Ns + "SesInfo")?.Value;
            var tokInfo = root.Element(Ns + "TokInfo")?.Value;

            if (!string.IsNullOrEmpty(sesInfo))
            {
                _sessionCookie = sesInfo;
                _log.LogInformation("HiLink {Ip}: Session cookie obtained", ip);
            }

            if (!string.IsNullOrEmpty(tokInfo))
            {
                _csrfToken = tokInfo;
                _log.LogInformation("HiLink {Ip}: CSRF token obtained", ip);
            }

            _isOpen = true;
            _log.LogInformation("HiLink {Ip}: Connected", ip);
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "HiLink {Ip}: Connection failed", ip);
            throw new InvalidOperationException($"HiLink connection failed at {ip}: {ex.Message}");
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
            _isOpen = false;
            return false;
        }
    }

    public async Task<string> GetImeiAsync()
    {
        if (!string.IsNullOrEmpty(_imei)) return _imei;
        var xml = await SendGetAsync("/api/device/information");
        if (string.IsNullOrEmpty(xml)) return string.Empty;

        var doc = XDocument.Parse(xml);
        _imei = doc.Root!.Element(Ns + "Imei")?.Value ?? string.Empty;
        _imsi = doc.Root!.Element(Ns + "Imsi")?.Value ?? string.Empty;
        return _imei;
    }

    public async Task<string> GetImsiAsync()
    {
        if (!string.IsNullOrEmpty(_imsi)) return _imsi;
        await GetImeiAsync();
        return _imsi ?? string.Empty;
    }

    public async Task<NetworkRegistration> GetNetworkRegistrationAsync()
    {
        var xml = await SendGetAsync("/api/monitoring/status");
        if (string.IsNullOrEmpty(xml)) return NetworkRegistration.Unknown;

        var doc = XDocument.Parse(xml);
        var status = doc.Root!.Element(Ns + "ConnectionStatus")?.Value;
        if (int.TryParse(status, out var val))
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
        _log.LogInformation("[HiLink] Phone USSD raw: {Raw}", resp);

        if (string.IsNullOrEmpty(resp)) return string.Empty;

        var phoneMatch = Regex.Match(resp, @"(\d{10,12})");
        return phoneMatch.Success ? phoneMatch.Value : string.Empty;
    }

    public Task<string> GetPhoneNumberViaCnumAsync() => Task.FromResult(string.Empty);

    public async Task<decimal?> GetBalanceAsync()
    {
        var balanceCode = _config.Get("modem.ussd.balance_code", "*222#");
        var resp = await SendUssdAsync(balanceCode, 15000);
        _log.LogInformation("[HiLink] Balance USSD raw: {Raw}", resp);

        if (string.IsNullOrEmpty(resp)) return null;

        if (!resp.Contains("Solde") && !resp.Contains("solde") && !resp.Contains("DA") && !resp.Contains("DZD"))
        {
            _log.LogWarning("[HiLink] Response does not contain balance: {Resp}", resp);
            return null;
        }

        var match = BalanceRegex().Match(resp);
        if (match.Success)
        {
            var amountStr = match.Groups[1].Value.Replace(",", ".");
            if (decimal.TryParse(amountStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var amount))
                return amount;
        }
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
            var msgElements = doc.Root?.Element(Ns + "Messages")?.Elements(Ns + "Message");
            if (msgElements == null) return messages;

            foreach (var el in msgElements)
            {
                var phone = el.Element(Ns + "Phone")?.Value ?? "";
                var content = el.Element(Ns + "Content")?.Value ?? "";
                var dateStr = el.Element(Ns + "Date")?.Value ?? "";
                var indexStr = el.Element(Ns + "Index")?.Value ?? "0";

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

            _log.LogInformation("[HiLink] Read {Count} SMS from inbox", messages.Count);
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
                _log.LogInformation("[HiLink] Deleted SMS index {Index}", msg.Index);
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
            var body = $@"<request><Code>{System.Security.SecurityElement.Escape(code)}</Code><Timeout>{timeoutMs}</Timeout></request>";
            var xml = await SendPostAsync("/api/ussd/send", body);
            if (string.IsNullOrEmpty(xml)) return string.Empty;

            var doc = XDocument.Parse(xml);
            var response = doc.Root?.Element(Ns + "Response")?.Value;
            if (!string.IsNullOrEmpty(response)) return response;

            var content = doc.Root?.Element(Ns + "Content")?.Value;
            if (!string.IsNullOrEmpty(content)) return content;

            return xml;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<bool> TrySetCharsetAsync(string charset) => Task.FromResult(true);

    public async Task<string> SendCommandAsync(string command, int timeoutMs = 5000)
    {
        _log.LogDebug("[HiLink] SendCommand (no-op): {Cmd}", command);
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
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_sessionCookie))
            request.Headers.Add("Cookie", $"SessionID={_sessionCookie}");

        if (!string.IsNullOrEmpty(_csrfToken))
            request.Headers.Add("__RequestVerificationToken", _csrfToken);
    }

    [GeneratedRegex(@"(\d+[\.,]?\d*)")]
    private static partial Regex BalanceRegex();
}
