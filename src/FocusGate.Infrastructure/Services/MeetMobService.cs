using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FocusGate.Core.DTOs;
using FocusGate.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

public partial class MeetMobService
{
    private readonly HttpClient _http;
    private readonly MeetMobTokenStore _tokenStore;
    private readonly ILogger<MeetMobService> _log;
    private readonly IConfigProvider _config;
    private readonly Dictionary<string, DateTime> _cooldowns = new();
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    public SemaphoreSlim RefreshLock { get; } = new(1, 1);

    private string BaseUrl => _config.Get("meetmob.base_url", "https://meetmob.mobilis.dz");
    private string Password => _config.Get("meetmob.password", "00000");
    private int OtpPollTimeout => _config.Get<int>("meetmob.otp_poll_timeout", 60);
    private int OtpPollInterval => _config.Get<int>("meetmob.otp_poll_interval", 3);
    private int TokenTtl => _config.Get<int>("meetmob.token_ttl", 2700);
    private int HttpTimeout => _config.Get<int>("meetmob.http_timeout", 10);
    private int LoginCooldown => _config.Get<int>("meetmob.login_cooldown", 120);
    private int FallbackCooldown => _config.Get<int>("meetmob.fallback_cooldown", 150);
    private DateTime _wafCooldownUntil = DateTime.MinValue;
    private bool _lastRequestNetworkError;
    private string? _lastErrorCode;

    public bool WasLastRequestNetworkError() => _lastRequestNetworkError;
    public string? GetLastErrorCode() => _lastErrorCode;

    public MeetMobService(MeetMobTokenStore tokenStore, ILogger<MeetMobService> log, IConfigProvider config)
    {
        _tokenStore = tokenStore;
        _log = log;
        _config = config;
        _http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        })
        {
            Timeout = TimeSpan.FromSeconds(HttpTimeout)
        };
    }

    private bool _warmedUp;

    public async Task WarmupAsync(CancellationToken ct)
    {
        if (_warmedUp) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl);
            using var resp = await _http.SendAsync(req, ct);
            _warmedUp = true;
            _log.LogInformation("MeetMob: TLS warmup OK ({Status})", resp.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogWarning("MeetMob: TLS warmup failed: {Error}", ex.Message);
        }
    }

    public async Task InvalidateTokenAsync(string key)
    {
        await _tokenStore.RemoveAsync(key);
        _log.LogInformation("MeetMob: Token invalidated for {Key}", key[..Math.Min(12, key.Length)]);
    }

    public async Task<bool> CanRetryAsync(string key)
    {
        if (!_cooldowns.TryGetValue(key, out var until))
            return true;
        if (DateTime.UtcNow >= until)
        {
            _cooldowns.Remove(key);
            return true;
        }
        return false;
    }

    public async Task SetCooldownAsync(string key, int seconds)
    {
        _cooldowns[key] = DateTime.UtcNow.AddSeconds(seconds);
        _log.LogInformation("MeetMob: Cooldown set for {Key} — {Seconds}s", key[..Math.Min(12, key.Length)], seconds);
    }

    public bool IsWafBlocked() => DateTime.UtcNow < _wafCooldownUntil;

    public void SetWafCooldown(int seconds = 300)
    {
        _wafCooldownUntil = DateTime.UtcNow.AddSeconds(seconds);
        _log.LogWarning("MeetMob: WAF cooldown set for {Seconds}s — Request Rejected / connection timeout", seconds);
    }

    public async Task<MeetMobToken?> GetValidTokenAsync(string key)
    {
        var token = await _tokenStore.GetAsync(key);
        if (token == null) return null;
        if (string.IsNullOrEmpty(token.CsrfToken) || string.IsNullOrEmpty(token.AccountId))
            return null;
        if (token.ExpiresAt < DateTime.UtcNow.AddMinutes(2))
        {
            _log.LogDebug("MeetMob: Token expired for {Key} (expired {Expired:F0}min ago)", key[..Math.Min(12, key.Length)], (DateTime.UtcNow - token.ExpiresAt).TotalMinutes);
            return null;
        }
        return token;
    }

    public async Task<MeetMobLoginResult> LoginAsync(string imsi, string phone, IAtCommandService at, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(phone))
            return new MeetMobLoginResult { Success = false, Error = "Invalid phone number" };

        await _loginLock.WaitAsync(ct);
        try
        {
            _log.LogInformation("MeetMob: Login starting for phone {Phone}", phone);

            var sendResult = await SendOtpAsync(phone, ct);
            if (!sendResult)
                return new MeetMobLoginResult { Success = false, Error = "sendSms failed" };

            _log.LogInformation("MeetMob: OTP sent, waiting before polling SIM inbox... phone={Phone}", phone);
            await Task.Delay(1500, ct);

            var otpCode = await WaitForOtpAsync(at, ct);
            if (string.IsNullOrEmpty(otpCode))
                return new MeetMobLoginResult { Success = false, Error = "OTP not received" };

            _log.LogInformation("MeetMob: OTP extracted: {Code}, logging in...", otpCode);

            var token = await LoginWithOtpAsync(phone, otpCode, ct);
            if (token == null)
                return new MeetMobLoginResult { Success = false, Error = "Login failed" };

            _log.LogInformation("MeetMob: Login success, waiting before subscriber data...");
            await Task.Delay(2000, ct);

            MeetMobSubscriberData? subData = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (IsWafBlocked())
                {
                    _log.LogWarning("MeetMob: Subscriber data skipped — WAF cooldown active");
                    break;
                }
                subData = await GetSubscriberDataAsync(token, ct);
                if (subData != null) break;
                _log.LogWarning("MeetMob: Subscriber data attempt {Attempt}/3 failed, retrying in 3s...", attempt + 1);
                await Task.Delay(3000, ct);
            }

            if (subData != null)
            {
                token.AccountId = subData.AccountId;
                token.SubscriberKey = subData.SubscriberKey;
            }

            token.Phone = phone;
            token.ExpiresAt = DateTime.UtcNow.AddSeconds(TokenTtl);
            await _tokenStore.SaveAsync(phone, token);

            _log.LogInformation("MeetMob: Login success for phone {Phone}, accountId={AccountId}", phone, token.AccountId);
            return new MeetMobLoginResult { Success = true, Token = token };
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private async Task<bool> SendOtpAsync(string phone, CancellationToken ct)
    {
        try
        {
            var body = new
            {
                locale = "en_US",
                password = Password,
                userName = phone,
                loginType = "01",
                ecareLoginType = "0"
            };
            using var doc = await PostJsonAsync($"{BaseUrl}/crm/ms/ecare/v1/login/sendSms", body, "EC021", ct);
            if (doc == null)
            {
                _log.LogWarning("MeetMob: sendSms HTTP failed for {Phone}", phone);
                return false;
            }
            if (doc.RootElement.GetProperty("result").GetString() != "success")
            {
                var raw = doc.RootElement.GetRawText();
                _log.LogWarning("MeetMob: sendSms non-success for {Phone}: {Resp}", phone, raw[..Math.Min(200, raw.Length)]);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MeetMob: sendSms failed for {Phone}", phone);
            return false;
        }
    }

    private async Task<string?> WaitForOtpAsync(IAtCommandService at, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(OtpPollTimeout);
        int consecutiveEmpty = 0;
        bool inboxCleared = false;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var messages = await at.ReadAllSmsAsync();
                foreach (var msg in messages)
                {
                    var code = ExtractOtpCode(msg.Content);
                    if (code != null)
                    {
                        _log.LogDebug("MeetMob: OTP found in SMS from {Sender}", msg.Sender);
                        return code;
                    }
                }
                consecutiveEmpty++;
                if (consecutiveEmpty >= 5 && !inboxCleared)
                {
                    _log.LogWarning("MeetMob: OTP poll — {Count} consecutive empty reads, clearing SMS inbox", consecutiveEmpty);
                    try { await at.DeleteAllSmsAsync(); } catch { }
                    inboxCleared = true;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogDebug(ex, "MeetMob: OTP poll read failed");
                if (IsWafBlocked())
                {
                    _log.LogWarning("MeetMob: OTP poll aborting — WAF cooldown active");
                    return null;
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(OtpPollInterval), ct); }
            catch (OperationCanceledException) { break; }
        }
        return null;
    }

    private async Task<MeetMobToken?> LoginWithOtpAsync(string phone, string otpCode, CancellationToken ct)
    {
        try
        {
            var body = new
            {
                imgCode = otpCode,
                locale = "en_US",
                password = Password,
                userName = phone,
                loginType = "01",
                ecareLoginType = "0"
            };
            using var doc = await PostJsonAsync($"{BaseUrl}/auth/user/login", body, "EC001", ct);
            if (doc == null) return null;

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success")
            {
                var errorMsg = SafeGetStringFromParent(root, "errorMessage", "unknown");
                _log.LogWarning("MeetMob: Login failed — {Error}", errorMsg);
                return null;
            }

            var resultBody = root.GetProperty("resultBody");
            var csrfToken = SafeGetStringFromParent(resultBody, "csrfToken");
            var cookie = ExtractCookieFromResponse();

            if (resultBody.TryGetProperty("pwdWillExpired", out var pwdExpired)
                && (pwdExpired.GetBoolean() || (pwdExpired.ValueKind == JsonValueKind.Number && pwdExpired.GetInt32() != 0)))
            {
                _log.LogInformation("MeetMob: pwdWillExpired — accepting disclaimer and re-logging in");
                await AcceptDisclaimerAsync(csrfToken, cookie, phone, ct);
                return null;
            }

            if (string.IsNullOrEmpty(csrfToken))
            {
                _log.LogWarning("MeetMob: Login returned empty csrfToken");
                return null;
            }

            return new MeetMobToken
            {
                CsrfToken = csrfToken,
                Cookie = cookie
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MeetMob: LoginWithOtp failed");
            return null;
        }
    }

    private async Task AcceptDisclaimerAsync(string csrfToken, string cookie, string phone, CancellationToken ct)
    {
        try
        {
            var body = new
            {
                ecareLoginType = "0",
                serviceNumber = phone,
                operType = "1",
                token = csrfToken
            };
            using var doc = await PostJsonAuthenticated($"{BaseUrl}/crm/ms/ecare/v1/login/agreeDisclaCtz", body, "EC107", csrfToken, cookie, phone, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MeetMob: acceptDisclaimer failed");
        }
    }

    private async Task<MeetMobSubscriberData?> GetSubscriberDataAsync(MeetMobToken token, CancellationToken ct)
    {
        try
        {
            using var doc = await PostJsonAuthenticated($"{BaseUrl}/crm/ms/ecare/v1/subscriber/querySubscriberData", new { }, "EC044", token.CsrfToken, token.Cookie, token.Phone, ct);
            if (doc == null)
            {
                _log.LogWarning("MeetMob: GetSubscriberData HTTP failed");
                return null;
            }

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success")
            {
                var raw = root.GetRawText();
                _log.LogWarning("MeetMob: GetSubscriberData non-success: {Resp}", raw[..Math.Min(200, raw.Length)]);
                return null;
            }

            var subInfo = root.GetProperty("resultBody").GetProperty("subInfo");
            return new MeetMobSubscriberData
            {
                SubscriberKey = SafeGetStringFromParent(subInfo, "subscriberId"),
                AccountId = SafeGetStringFromParent(subInfo, "accountId")
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MeetMob: GetSubscriberData failed");
            return null;
        }
    }

    public async Task<decimal?> GetBalanceAsync(string imsi, MeetMobToken token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token.AccountId))
        {
            if (IsWafBlocked())
            {
                _log.LogWarning("MeetMob: No accountId for phone {Phone} and WAF is blocking — skipping", token.Phone);
                return null;
            }
            _log.LogWarning("MeetMob: No accountId for phone {Phone}, re-fetching subscriber data", token.Phone);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var subData = await GetSubscriberDataAsync(token, ct);
                if (subData != null)
                {
                    token.AccountId = subData.AccountId;
                    token.SubscriberKey = subData.SubscriberKey;
                    await _tokenStore.SaveAsync(token.Phone, token);
                    break;
                }
                if (attempt < 2)
                {
                    if (IsWafBlocked()) break;
                    _log.LogWarning("MeetMob: Subscriber data attempt {Attempt}/3 failed for balance, retrying in 3s...", attempt + 1);
                    await Task.Delay(3000, ct);
                }
            }
            if (string.IsNullOrEmpty(token.AccountId)) return null;
        }

        try
        {
            var body = new { accessInfos = new { code = "2", value = token.AccountId } };
            using var doc = await PostJsonAuthenticated($"{BaseUrl}/crm/ms/ecare/v1/billing/queryBalance", body, "EC046", token.CsrfToken, token.Cookie, token.Phone, ct);
            if (doc == null)
            {
                _lastErrorCode = "NETWORK_ERROR";
                _log.LogWarning("MeetMob: GetBalance HTTP failed for IMSI {Imsi}", imsi[..Math.Min(8, imsi.Length)]);
                return null;
            }

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success")
            {
                var raw = root.GetRawText();
                _lastErrorCode = SafeGetStringFromParent(root, "errorCode", "UNKNOWN");
                _log.LogWarning("MeetMob: GetBalance non-success for IMSI {Imsi} (error={Error}): {Resp}", imsi[..Math.Min(8, imsi.Length)], _lastErrorCode, raw[..Math.Min(200, raw.Length)]);
                return null;
            }

            _lastErrorCode = null;

            var balanceInfo = root.GetProperty("resultBody").GetProperty("balanceInfomation");
            var amountStr = SafeGetStringFromParent(balanceInfo, "advancedAmount", "0");
            amountStr = NormalizeMeetMobAmount(amountStr);
            if (decimal.TryParse(amountStr, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var balance))
                return balance;

            _log.LogWarning("MeetMob: GetBalance parse failed for IMSI {Imsi}: raw='{Raw}'", imsi[..Math.Min(8, imsi.Length)], amountStr);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MeetMob: GetBalance failed for IMSI {Imsi}", imsi[..Math.Min(8, imsi.Length)]);
            return null;
        }
    }

    public async Task<List<MeetMobRechargeRecord>> GetRechargeHistoryAsync(MeetMobToken token, CancellationToken ct)
    {
        try
        {
            using var doc = await PostJsonAuthenticated($"{BaseUrl}/crm/ms/ecare/v1/ecare/queryRechargeHistory", new { }, "EC049", token.CsrfToken, token.Cookie, token.Phone, ct);
            if (doc == null)
            {
                _log.LogWarning("MeetMob: GetRechargeHistory HTTP failed");
                return new();
            }

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success")
            {
                var raw = root.GetRawText();
                _log.LogWarning("MeetMob: GetRechargeHistory non-success: {Resp}", raw[..Math.Min(200, raw.Length)]);
                return new();
            }

            var records = new List<MeetMobRechargeRecord>();
            if (root.GetProperty("resultBody").TryGetProperty("rechargeInfo", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    records.Add(new MeetMobRechargeRecord
                    {
                        TradeTime = item.GetProperty("tradeTime").GetString() ?? "",
                        Amount = SafeGetStringFromParent(item, "rechargeAmount", "0")
                    });
                }
            }
            _log.LogInformation("MeetMob: GetRechargeHistory returned {Count} records", records.Count);
            return records;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MeetMob: GetRechargeHistory failed");
            return new();
        }
    }

    public async Task<MeetMobFreeResource?> GetFreeResourceAsync(MeetMobToken token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token.SubscriberKey)) return null;
        try
        {
            var body = new { queryObj = new { subAccessCode = new { subscriberKey = token.SubscriberKey } } };
            using var doc = await PostJsonAuthenticated($"{BaseUrl}/crm/ms/ecare/v1/ecare/queryFreeResource", body, "EC048", token.CsrfToken, token.Cookie, token.Phone, ct);
            if (doc == null) return null;

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success") return null;

            var rb = root.GetProperty("resultBody");
            return new MeetMobFreeResource
            {
                VoiceLeft = SafeGetStringFromParent(rb, "voiceLeftAmount", "0"),
                DataLeft = SafeGetStringFromParent(rb, "dataLeftAmount", "0"),
                SmsLeft = SafeGetStringFromParent(rb, "smsLeftAmount", "0")
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MeetMob: GetFreeResource failed");
            return null;
        }
    }

    public async Task<MeetMobCustomerInfo?> GetCustomerInfoAsync(MeetMobToken token, CancellationToken ct)
    {
        try
        {
            using var doc = await PostJsonAuthenticated($"{BaseUrl}/crm/ms/ecare/v1/customer/customerInfo", new { }, "EC041", token.CsrfToken, token.Cookie, token.Phone, ct);
            if (doc == null) return null;

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success") return null;

            var rb = root.GetProperty("resultBody");
            return new MeetMobCustomerInfo
            {
                CustomerId = SafeGetStringFromParent(rb, "custId"),
                FirstName = SafeGetStringFromParent(rb, "firstName"),
                LastName = SafeGetStringFromParent(rb, "lastName")
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MeetMob: GetCustomerInfo failed");
            return null;
        }
    }

    private async Task<JsonDocument?> PostJsonAsync(string url, object body, string busiType, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                ApplyBrowserHeaders(request, busiType, null, null, null);

                var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("MeetMob: HTTP {Status} from {Url}", response.StatusCode, url);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                if (json.Length > 0 && json[0] == '<')
                {
                    _log.LogWarning("MeetMob: Got HTML response from {Url} (first 200 chars): {Snippet}", url, json[..Math.Min(200, json.Length)]);
                    SetWafCooldown(300);
                    return null;
                }
                return JsonDocument.Parse(json);
            }
            catch (HttpRequestException ex) when (attempt < 2 && !ct.IsCancellationRequested)
            {
                _log.LogWarning("MeetMob: HTTP request failed (attempt {Attempt}/3): {Error}", attempt + 1, ex.Message);
                await Task.Delay(2000, ct);
            }
        }
        return null;
    }

    private async Task<JsonDocument?> PostJsonAuthenticated(string url, object body, string busiType, string csrfToken, string cookie, string phone, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                ApplyBrowserHeaders(request, busiType, csrfToken, cookie, phone);

                var response = await _http.SendAsync(request, ct);
                _lastRequestNetworkError = false;
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("MeetMob: HTTP {Status} from {Url}", response.StatusCode, url);
                    return null;
                }

                UpdateCookieFromResponse(response);
                var json = await response.Content.ReadAsStringAsync(ct);
                if (json.Length > 0 && json[0] == '<')
                {
                    _log.LogWarning("MeetMob: Got HTML response from {Url} (first 200 chars): {Snippet}", url, json[..Math.Min(200, json.Length)]);
                    SetWafCooldown(300);
                    return null;
                }
                return JsonDocument.Parse(json);
            }
            catch (HttpRequestException ex) when (attempt < 2 && !ct.IsCancellationRequested)
            {
                _lastRequestNetworkError = true;
                _log.LogWarning("MeetMob: Authenticated HTTP request failed (attempt {Attempt}/3): {Error}", attempt + 1, ex.Message);
                await Task.Delay(2000, ct);
            }
        }
        return null;
    }

    private void ApplyBrowserHeaders(HttpRequestMessage request, string busiType, string? csrfToken, string? cookie, string? phone)
    {
        request.Headers.Add("Accept", "application/json, text/plain, */*");
        request.Headers.Add("Referer", "https://meetmob.mobilis.dz/EcareWeb/");
        request.Headers.Add("lang", "en_US");
        request.Headers.Add("locale", "en_US");
        request.Headers.Add("US-BUSI-TYPE", busiType);
        request.Headers.Add("HTTP_X_MSISDN", phone ?? "");

        if (!string.IsNullOrEmpty(csrfToken))
            request.Headers.Add("csrfToken", csrfToken);
        if (!string.IsNullOrEmpty(cookie))
            request.Headers.Add("Cookie", cookie);

        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        request.Headers.Add("Origin", "https://meetmob.mobilis.dz");
        request.Headers.Add("Sec-Fetch-Dest", "empty");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
    }

    private string _sessionCookie = "";

    private void UpdateCookieFromResponse(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                var parts = cookie.Split(';')[0].Trim();
                if (parts.Contains('='))
                {
                    if (string.IsNullOrEmpty(_sessionCookie))
                        _sessionCookie = parts;
                    else
                        _sessionCookie += "; " + parts;
                }
            }
        }
    }

    private string ExtractCookieFromResponse() => _sessionCookie;

    internal static string NormalizeMeetMobAmount(string amount) =>
        amount.Replace(" ", "").Replace(",", ".");

    private static string SafeGetString(JsonElement element, string defaultValue = "")
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString() ?? defaultValue;
        if (element.ValueKind == JsonValueKind.Number) return element.GetRawText();
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined) return defaultValue;
        return element.ToString();
    }

    private static string SafeGetStringFromParent(JsonElement parent, string property, string defaultValue = "")
    {
        if (!parent.TryGetProperty(property, out var elem)) return defaultValue;
        return SafeGetString(elem, defaultValue);
    }

    public static string? ExtractOtpCode(string content)
    {
        var match = OtpRegex().Match(content);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string? FormatPhone(long phoneNumber)
    {
        var str = phoneNumber.ToString();
        if (str.StartsWith("213") && str.Length >= 10)
            return "0" + str[3..];
        if (str.StartsWith("0") && str.Length >= 10)
            return str;
        if (str.Length == 9)
            return "0" + str;
        return str;
    }

    [GeneratedRegex(@"verification code.*?is (\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex OtpRegex();
}

public class MeetMobLoginResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public MeetMobToken? Token { get; set; }
}

public class MeetMobSubscriberData
{
    public string SubscriberKey { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
}

public class MeetMobRechargeRecord
{
    public string TradeTime { get; set; } = string.Empty;
    public string Amount { get; set; } = "0";
}

public class MeetMobFreeResource
{
    public string VoiceLeft { get; set; } = "0";
    public string DataLeft { get; set; } = "0";
    public string SmsLeft { get; set; } = "0";
}

public class MeetMobCustomerInfo
{
    public string CustomerId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}
