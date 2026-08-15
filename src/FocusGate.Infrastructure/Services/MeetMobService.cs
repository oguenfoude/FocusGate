using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
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
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    private readonly ConcurrentDictionary<string, DateTime> _wafCooldowns = new();
    private readonly ConcurrentDictionary<string, int> _wafConsecutiveBlocks = new();
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    public MeetMobTokenStore TokenStore => _tokenStore;

    public SemaphoreSlim RefreshLock { get; } = new(1, 1);

    private string BaseUrl => _config.Get("meetmob.base_url", "https://meetmob.mobilis.dz");
    private string Password => _config.Get("meetmob.password", "00000");
    private int OtpPollTimeout => _config.Get<int>("meetmob.otp_poll_timeout", 60);
    private int OtpPollInterval => _config.Get<int>("meetmob.otp_poll_interval", 1);
    private int TokenTtl => _config.Get<int>("meetmob.token_ttl", 2700);
    private int HttpTimeout => _config.Get<int>("meetmob.http_timeout", 7);
    private int LoginCooldown => _config.Get<int>("meetmob.login_cooldown", 3);
    private int FallbackCooldown => _config.Get<int>("meetmob.fallback_cooldown", 3);
    private long _wafCooldownUntilUtcTicks; // Legacy global WAF cooldown (kept for backward compatibility)
    private readonly ConcurrentDictionary<string, bool> _lastRequestNetworkErrors = new();
    private readonly ConcurrentDictionary<string, string?> _lastErrorCodes = new();
    private readonly SemaphoreSlim _httpThrottle = new(3, 3); // Max 3 concurrent HTTP requests to MeetMob
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static readonly object _rateLimitLock = new();

    private static async Task ThrottleGlobalAsync(CancellationToken ct)
    {
        lock (_rateLimitLock)
        {
            var elapsed = DateTime.UtcNow - _lastRequestUtc;
            if (elapsed < TimeSpan.FromMilliseconds(500))
            {
                var wait = TimeSpan.FromMilliseconds(500) - elapsed;
                Task.Delay(wait, ct).Wait(ct);
            }
            _lastRequestUtc = DateTime.UtcNow;
        }
        await Task.CompletedTask;
    }

    public bool WasLastRequestNetworkError(string? key = null)
    {
        if (key != null && _lastRequestNetworkErrors.TryGetValue(key, out var val))
            return val;
        return _lastRequestNetworkErrors.Values.Any(v => v);
    }
    public string? GetLastErrorCode(string? key = null)
    {
        if (key != null && _lastErrorCodes.TryGetValue(key, out var val))
            return val;
        return _lastErrorCodes.Values.FirstOrDefault(v => v != null);
    }

    public MeetMobService(MeetMobTokenStore tokenStore, ILogger<MeetMobService> log, IConfigProvider config)
    {
        _tokenStore = tokenStore;
        _log = log;
        _config = config;

        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            },
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        _http = new HttpClient(handler)
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl);
            using var resp = await _http.SendAsync(req, cts.Token);
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

    }

    public bool CanRetry(string key)
    {
        if (!_cooldowns.TryGetValue(key, out var until))
            return true;
        if (DateTime.UtcNow >= until)
        {
            _cooldowns.TryRemove(key, out _);
            return true;
        }
        return false;
    }

    public void SetCooldown(string key, int seconds)
    {
        var jitteredSeconds = seconds + Random.Shared.Next(0, Math.Max(1, seconds / 4));
        _cooldowns[key] = DateTime.UtcNow.AddSeconds(jitteredSeconds);
        _log.LogDebug("MeetMob [{Phone}]: Cooldown {Seconds}s", key, jitteredSeconds);
    }

    public bool IsWafBlocked(string? phone = null)
    {
        // Per-phone WAF cooldown (new)
        if (!string.IsNullOrEmpty(phone) && _wafCooldowns.TryGetValue(phone, out var until) && DateTime.UtcNow < until)
            return true;
        // Global WAF cooldown (legacy fallback)
        return DateTime.UtcNow.Ticks < Interlocked.Read(ref _wafCooldownUntilUtcTicks);
    }

    public TimeSpan GetWafCooldownRemaining(string? phone = null)
    {
        if (!string.IsNullOrEmpty(phone) && _wafCooldowns.TryGetValue(phone, out var until))
        {
            var remaining = until - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        var globalTicks = Interlocked.Read(ref _wafCooldownUntilUtcTicks);
        if (globalTicks > 0)
        {
            var globalUntil = new DateTime(globalTicks, DateTimeKind.Utc);
            var remaining = globalUntil - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return TimeSpan.Zero;
    }

    public void SetWafCooldown(string? phone = null, int seconds = 60)
    {
        if (!string.IsNullOrEmpty(phone))
        {
            var consecutive = _wafConsecutiveBlocks.AddOrUpdate(phone, 1, (_, old) => old + 1);
            // Exponential backoff: 60s, 120s, 180s (capped)
            var effectiveSeconds = Math.Min(seconds * (1 << (consecutive - 1)), 180);
            var jitteredSeconds = effectiveSeconds + Random.Shared.Next(0, 10);
            var newExpiry = DateTime.UtcNow.AddSeconds(jitteredSeconds);
            if (_wafCooldowns.TryGetValue(phone, out var existing) && existing > newExpiry)
            {
                return;
            }
            _wafCooldowns[phone] = newExpiry;
            _log.LogWarning("MeetMob: WAF {Phone} — {Seconds}s (n={Consecutive})", phone, jitteredSeconds, consecutive);
        }
        else
        {
            var jitteredSeconds = seconds + Random.Shared.Next(0, 10);
            Interlocked.Exchange(ref _wafCooldownUntilUtcTicks, DateTime.UtcNow.AddSeconds(jitteredSeconds).Ticks);
        }
    }

    public void ResetWafConsecutiveBlocks(string? phone = null)
    {
        if (!string.IsNullOrEmpty(phone))
            _wafConsecutiveBlocks.TryRemove(phone, out _);
    }

    public int CleanupStaleEntries()
    {
        var now = DateTime.UtcNow;
        var cleaned = 0;

        // Clean expired WAF cooldowns
        foreach (var kvp in _wafCooldowns)
        {
            if (kvp.Value < now)
            {
                _wafCooldowns.TryRemove(kvp.Key, out _);
                _wafConsecutiveBlocks.TryRemove(kvp.Key, out _);
                cleaned++;
            }
        }

        // Clean expired regular cooldowns
        foreach (var kvp in _cooldowns)
        {
            if (kvp.Value < now)
            {
                _cooldowns.TryRemove(kvp.Key, out _);
                cleaned++;
            }
        }

        // Clean stale network error / error code entries for phones no longer in cooldown
        foreach (var kvp in _lastRequestNetworkErrors)
        {
            if (!_wafCooldowns.ContainsKey(kvp.Key) && !_cooldowns.ContainsKey(kvp.Key))
            {
                _lastRequestNetworkErrors.TryRemove(kvp.Key, out _);
                _lastErrorCodes.TryRemove(kvp.Key, out _);
                cleaned++;
            }
        }

        return cleaned;
    }

    public async Task<MeetMobToken?> GetValidTokenAsync(string key)
    {
        var token = await _tokenStore.GetAsync(key);
        if (token == null) return null;
        if (string.IsNullOrEmpty(token.CsrfToken) || string.IsNullOrEmpty(token.AccountId))
            return null;
        if (token.ExpiresAt < DateTime.UtcNow.AddMinutes(2))
        {
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
            _lastRequestNetworkErrors.Clear();
            _lastErrorCodes.Clear();
            _log.LogInformation("MeetMob [{Phone}]: Login starting (IMSI={Imsi})", phone, imsi[..Math.Min(10, imsi.Length)]);

            var sendResult = await SendOtpAsync(phone, ct);
            if (!sendResult)
                return new MeetMobLoginResult { Success = false, Error = "sendSms failed" };

            _log.LogInformation("MeetMob [{Phone}]: OTP sent, polling SIM inbox...", phone);
            await Task.Delay(300, ct);

            var otpCode = await WaitForOtpAsync(at, phone, ct);
            if (string.IsNullOrEmpty(otpCode))
                return new MeetMobLoginResult { Success = false, Error = "OTP not received" };

            _log.LogInformation("MeetMob [{Phone}]: OTP extracted: {Code}, clearing SMS from SIM...", phone, otpCode);
            try { await at.DeleteAllSmsAsync(); } catch { }

            var token = await LoginWithOtpAsync(phone, otpCode, ct);
            if (token == null)
                return new MeetMobLoginResult { Success = false, Error = "Login failed" };

            _log.LogInformation("MeetMob [{Phone}]: Login success, fetching subscriber data...", phone);

            MeetMobSubscriberData? subData = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (IsWafBlocked(phone))
                {
                    _log.LogWarning("MeetMob [{Phone}]: Subscriber data skipped — WAF cooldown active", phone);
                    break;
                }
                subData = await GetSubscriberDataAsync(token, ct);
                if (subData != null) break;
                await Task.Delay(300, ct);
            }

            if (subData != null)
            {
                token.AccountId = subData.AccountId;
                token.SubscriberKey = subData.SubscriberKey;
            }

            token.Phone = phone;
            token.ExpiresAt = DateTime.UtcNow.AddSeconds(TokenTtl);
            await _tokenStore.SaveAsync(phone, token);

            _log.LogInformation("MeetMob [{Phone}]: Login complete — accountId={AccountId}, expires={Expiry}", phone, token.AccountId, token.ExpiresAt);
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
            var (doc, response) = await PostJsonAsync($"{BaseUrl}/crm/ms/ecare/v1/login/sendSms", body, "EC021", ct, phone);
            response?.Dispose();
            if (doc == null)
            {
                _log.LogWarning("MeetMob [{Phone}]: sendSms HTTP failed", phone);
                return false;
            }
            if (doc.RootElement.GetProperty("result").GetString() != "success")
            {
                var raw = doc.RootElement.GetRawText();
                var errorCode = doc.RootElement.TryGetProperty("code", out var codeEl) ? codeEl.GetString() ?? "" : "";
                _log.LogWarning("MeetMob [{Phone}]: sendSms non-success (code={Code}): {Resp}", phone, errorCode, raw[..Math.Min(200, raw.Length)]);
                if (errorCode.Contains("100206"))
                    SetCooldown(phone, 120);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("MeetMob [{Phone}]: sendSms failed: {Error}", phone, ex.Message);
            return false;
        }
    }

    private async Task<string?> WaitForOtpAsync(IAtCommandService at, string phone, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(OtpPollTimeout);
        int consecutiveEmpty = 0;
        bool inboxCleared = false;
        _log.LogDebug("MeetMob [{Phone}]: Polling OTP (timeout={Timeout}s)", phone, OtpPollTimeout);
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
                        return code;
                    }
                }
                consecutiveEmpty++;
                if (consecutiveEmpty >= 5 && !inboxCleared)
                {
                    _log.LogWarning("MeetMob [{Phone}]: OTP poll — {Count} consecutive empty reads, clearing SMS inbox", phone, consecutiveEmpty);
                    try { await at.DeleteAllSmsAsync(); } catch { }
                    inboxCleared = true;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "MeetMob [{Phone}]: OTP poll failed", phone);
                if (IsWafBlocked(phone))
                {
                    return null;
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(OtpPollInterval), ct); }
            catch (OperationCanceledException) { break; }
        }
        return null;
    }

    private async Task<MeetMobToken?> LoginWithOtpAsync(string phone, string otpCode, CancellationToken ct, bool isRetry = false)
    {
        HttpResponseMessage? response = null;
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
            var (doc, resp) = await PostJsonAsync($"{BaseUrl}/auth/user/login", body, "EC001", ct, phone);
            response = resp;
            if (doc == null) return null;

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success")
            {
                var errorMsg = SafeGetStringFromParent(root, "errorMessage", "unknown");
                _log.LogWarning("MeetMob [{Phone}]: OTP login failed — {Error}", phone, errorMsg);
                return null;
            }

            var resultBody = root.GetProperty("resultBody");
            var csrfToken = SafeGetStringFromParent(resultBody, "csrfToken");
            var cookie = ExtractCookiesFromResponse(response);

            if (resultBody.TryGetProperty("pwdWillExpired", out var pwdExpired)
                && (pwdExpired.GetBoolean() || (pwdExpired.ValueKind == JsonValueKind.Number && pwdExpired.GetInt32() != 0)))
            {
                if (isRetry)
                {
                    _log.LogWarning("MeetMob [{Phone}]: pwdWillExpired persisted after disclaimer — returning null", phone);
                    return null;
                }
                _log.LogInformation("MeetMob [{Phone}]: pwdWillExpired — accepting disclaimer, retrying login", phone);
                await AcceptDisclaimerAsync(csrfToken, cookie, phone, ct);
                return await LoginWithOtpAsync(phone, otpCode, ct, isRetry: true);
            }

            if (string.IsNullOrEmpty(csrfToken))
            {
                _log.LogWarning("MeetMob [{Phone}]: OTP login returned empty csrfToken", phone);
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
            _log.LogWarning("MeetMob [{Phone}]: OTP login exception: {Error}", phone, ex.Message);
            return null;
        }
        finally
        {
            response?.Dispose();
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
            _log.LogInformation("MeetMob [{Phone}]: Disclaimer accepted", phone);
        }
        catch (Exception ex)
        {
            _log.LogWarning("MeetMob [{Phone}]: Disclaimer failed: {Error}", phone, ex.Message);
        }
    }

    private async Task<MeetMobSubscriberData?> GetSubscriberDataAsync(MeetMobToken token, CancellationToken ct)
    {
        var phone = token.Phone ?? "unknown";
        try
        {
            using var doc = await PostJsonAuthenticated($"{BaseUrl}/crm/ms/ecare/v1/subscriber/querySubscriberData", new { }, "EC044", token.CsrfToken, token.Cookie, phone, ct);
            if (doc == null)
            {
                _log.LogWarning("MeetMob [{Phone}]: Subscriber data HTTP failed", phone);
                return null;
            }

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success")
            {
                var raw = root.GetRawText();
                _log.LogWarning("MeetMob [{Phone}]: Subscriber data non-success: {Resp}", phone, raw[..Math.Min(200, raw.Length)]);
                return null;
            }

            var subInfo = root.GetProperty("resultBody").GetProperty("subInfo");
            var result = new MeetMobSubscriberData
            {
                SubscriberKey = SafeGetStringFromParent(subInfo, "subscriberId"),
                AccountId = SafeGetStringFromParent(subInfo, "accountId")
            };
            _log.LogInformation("MeetMob [{Phone}]: Subscriber data OK — accountId={AccountId}", phone, result.AccountId);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning("MeetMob: GetSubscriberData failed: {Error}", ex.Message);
            return null;
        }
    }

    public async Task<decimal?> GetBalanceAsync(MeetMobToken token, CancellationToken ct)
    {
        var phone = token.Phone ?? "unknown";
        if (string.IsNullOrEmpty(token.AccountId))
        {
            if (IsWafBlocked(phone))
            {
                _log.LogWarning("MeetMob [{Phone}]: No accountId and WAF blocking — skipping", phone);
                return null;
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var subData = await GetSubscriberDataAsync(token, ct);
                if (subData != null)
                {
                    token.AccountId = subData.AccountId;
                    token.SubscriberKey = subData.SubscriberKey;
                    await _tokenStore.SaveAsync(phone, token);
                    break;
                }
                if (attempt < 2)
                {
                    if (IsWafBlocked(phone)) break;
                    _log.LogWarning("MeetMob [{Phone}]: Subscriber data attempt {Attempt}/3 failed, retrying...", phone, attempt + 1);
                    await Task.Delay(300, ct);
                }
            }
            if (string.IsNullOrEmpty(token.AccountId)) return null;
        }

        try
        {
            var body = new { accessInfos = new { code = "2", value = token.AccountId } };
            using var doc = await PostJsonAuthenticated($"{BaseUrl}/crm/ms/ecare/v1/billing/queryBalance", body, "EC046", token.CsrfToken, token.Cookie, phone, ct);
            if (doc == null)
            {
                _lastErrorCodes[phone] = "NETWORK_ERROR";
                _log.LogWarning("MeetMob [{Phone}]: Balance HTTP failed (network/WAF)", phone);
                return null;
            }

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success")
            {
                var raw = root.GetRawText();
                _lastErrorCodes[phone] = SafeGetStringFromParent(root, "errorCode", "UNKNOWN");
                _log.LogWarning("MeetMob [{Phone}]: Balance non-success (error={Error}): {Resp}", phone, _lastErrorCodes[phone], raw[..Math.Min(200, raw.Length)]);
                return null;
            }

            _lastErrorCodes[phone] = null;

            string amountStr = "";
            if (root.TryGetProperty("resultBody", out var rb))
            {
                // 1. Modern MeetMob format (acctList -> balanceResult -> totalAmount)
                if (rb.TryGetProperty("acctList", out var acctList) && acctList.ValueKind == JsonValueKind.Array && acctList.GetArrayLength() > 0)
                {
                    var firstAcct = acctList[0];
                    if (firstAcct.TryGetProperty("balanceResult", out var balRes) && balRes.ValueKind == JsonValueKind.Array && balRes.GetArrayLength() > 0)
                    {
                        var firstBal = balRes[0];
                        amountStr = SafeGetStringFromParent(firstBal, "totalAmount");
                        if (string.IsNullOrEmpty(amountStr) && firstBal.TryGetProperty("balanceDetail", out var balDet) && balDet.ValueKind == JsonValueKind.Array && balDet.GetArrayLength() > 0)
                            amountStr = SafeGetStringFromParent(balDet[0], "amount");
                    }
                }

                // 2. Legacy fallback (balanceInfomation -> advancedAmount)
                if (string.IsNullOrEmpty(amountStr) && rb.TryGetProperty("balanceInfomation", out var balInfo))
                {
                    amountStr = SafeGetStringFromParent(balInfo, "advancedAmount");
                }

                // 3. Direct property fallback
                if (string.IsNullOrEmpty(amountStr))
                {
                    amountStr = SafeGetStringFromParent(rb, "advancedAmount");
                    if (string.IsNullOrEmpty(amountStr))
                        amountStr = SafeGetStringFromParent(rb, "totalAmount");
                }
            }

            amountStr = NormalizeMeetMobAmount(amountStr);
            if (decimal.TryParse(amountStr, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var balance))
            {
                return balance;
            }

            _log.LogWarning("MeetMob [{Phone}]: Balance parse failed — raw='{Raw}'", phone, amountStr);
            return null;
        }
        catch (Exception ex)
        {
            _lastErrorCodes[phone] = "NETWORK_ERROR";
            _log.LogWarning("MeetMob [{Phone}]: Balance request failed: {Error}", phone, ex.Message);
            return null;
        }
    }

    public async Task<List<MeetMobRechargeRecord>> GetRechargeHistoryAsync(MeetMobToken token, CancellationToken ct)
    {
        var phone = token.Phone ?? "unknown";
        try
        {
            using var doc = await PostJsonAuthenticated($"{BaseUrl}/crm/ms/ecare/v1/ecare/queryRechargeHistory", new { }, "EC049", token.CsrfToken, token.Cookie, phone, ct);
            if (doc == null)
            {
                _log.LogWarning("MeetMob [{Phone}]: Recharge history HTTP failed", phone);
                return new();
            }

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success")
            {
                var raw = root.GetRawText();
                _log.LogWarning("MeetMob [{Phone}]: Recharge history non-success: {Resp}", phone, raw[..Math.Min(200, raw.Length)]);
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
            if (records.Count > 0)
            {
                _log.LogInformation("MeetMob [{Phone}]: Recharge history — {Count} records", phone, records.Count);
            }
            else
            {
                _log.LogInformation("MeetMob [{Phone}]: Recharge history — 0 records", phone);
            }
            return records;
        }
        catch (Exception ex)
        {
            _log.LogWarning("MeetMob [{Phone}]: Recharge history failed: {Error}", phone, ex.Message);
            return new();
        }
    }

    public async Task<MeetMobFreeResource?> GetFreeResourceAsync(MeetMobToken token, CancellationToken ct)
    {
        var phone = token.Phone ?? "unknown";
        if (string.IsNullOrEmpty(token.SubscriberKey))
        {
            return null;
        }
        try
        {
            var body = new { queryObj = new { subAccessCode = new { subscriberKey = token.SubscriberKey } } };
            using var doc = await PostJsonAuthenticated($"{BaseUrl}/crm/ms/ecare/v1/ecare/queryFreeResource", body, "EC048", token.CsrfToken, token.Cookie, phone, ct);
            if (doc == null) return null;

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success") return null;

            var rb = root.GetProperty("resultBody");
            var result = new MeetMobFreeResource
            {
                VoiceLeft = SafeGetStringFromParent(rb, "voiceLeftAmount", "0"),
                DataLeft = SafeGetStringFromParent(rb, "dataLeftAmount", "0"),
                SmsLeft = SafeGetStringFromParent(rb, "smsLeftAmount", "0")
            };
            _log.LogInformation("MeetMob [{Phone}]: Free resources — Voice={Voice} Data={Data} SMS={Sms}", phone, result.VoiceLeft, result.DataLeft, result.SmsLeft);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning("MeetMob [{Phone}]: FreeResource failed: {Error}", phone, ex.Message);
            return null;
        }
    }

    public async Task<MeetMobCustomerInfo?> GetCustomerInfoAsync(MeetMobToken token, CancellationToken ct)
    {
        var phone = token.Phone ?? "unknown";
        try
        {
            using var doc = await PostJsonAuthenticated($"{BaseUrl}/crm/ms/ecare/v1/customer/customerInfo", new { }, "EC041", token.CsrfToken, token.Cookie, phone, ct);
            if (doc == null) return null;

            var root = doc.RootElement;
            if (root.GetProperty("result").GetString() != "success") return null;

            var rb = root.GetProperty("resultBody");
            var result = new MeetMobCustomerInfo
            {
                CustomerId = SafeGetStringFromParent(rb, "custId"),
                FirstName = SafeGetStringFromParent(rb, "firstName"),
                LastName = SafeGetStringFromParent(rb, "lastName")
            };
            _log.LogInformation("MeetMob [{Phone}]: Customer — {FirstName} {LastName} (ID={CustomerId})", phone, result.FirstName, result.LastName, result.CustomerId);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning("MeetMob [{Phone}]: CustomerInfo failed: {Error}", phone, ex.Message);
            return null;
        }
    }

    private async Task<(JsonDocument? doc, HttpResponseMessage? response)> PostJsonAsync(string url, object body, string busiType, CancellationToken ct, string? phone = null)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await ThrottleGlobalAsync(ct);
                // Global throttle: max 3 concurrent HTTP requests to MeetMob
                await _httpThrottle.WaitAsync(ct);
                HttpResponseMessage response;
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                    };
                    ApplyBrowserHeaders(request, busiType, null, null, null);

                    response = await _http.SendAsync(request, ct);
                }
                finally { _httpThrottle.Release(); }

                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("MeetMob [{Phone}]: HTTP {Status} from {Url}", phone ?? "?", response.StatusCode, url);
                    return (null, response);
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                if (json.Length > 0 && json[0] == '<')
                {
                    _log.LogWarning("MeetMob [{Phone}]: WAF block — HTML response from {Url} ({Length} bytes)", phone ?? "?", url, json.Length);
                    SetWafCooldown(phone);
                    return (null, response);
                }
                return (JsonDocument.Parse(json), response);
            }
            catch (HttpRequestException ex) when (attempt < 1 && !ct.IsCancellationRequested)
            {
                _log.LogWarning("MeetMob [{Phone}]: {BusiType} HTTP error (attempt {Attempt}/2): {Error}", phone ?? "?", busiType, attempt + 1, ex.Message);
                await Task.Delay(300, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("MeetMob [{Phone}]: {BusiType} timeout ({Timeout}s)", phone ?? "?", busiType, HttpTimeout);
                return (null, null);
            }
        }
        return (null, null);
    }

    private async Task<JsonDocument?> PostJsonAuthenticated(string url, object body, string busiType, string csrfToken, string cookie, string phone, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await ThrottleGlobalAsync(ct);
                // Global throttle: max 3 concurrent HTTP requests to MeetMob
                await _httpThrottle.WaitAsync(ct);
                HttpResponseMessage response;
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                    };
                    ApplyBrowserHeaders(request, busiType, csrfToken, cookie, phone);

                    response = await _http.SendAsync(request, ct);
                }
                finally { _httpThrottle.Release(); }

                _lastRequestNetworkErrors[phone] = false;
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("MeetMob [{Phone}]: HTTP {Status} from {Url}", phone, response.StatusCode, url);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                if (json.Length > 0 && json[0] == '<')
                {
                    _log.LogWarning("MeetMob [{Phone}]: WAF block — HTML response from {Url} ({Length} bytes)", phone, url, json.Length);
                    SetWafCooldown(phone);
                    return null;
                }
                ResetWafConsecutiveBlocks(phone);
                return JsonDocument.Parse(json);
            }
            catch (HttpRequestException ex) when (attempt < 1 && !ct.IsCancellationRequested)
            {
                _log.LogWarning("MeetMob [{Phone}]: {BusiType} HTTP error (attempt {Attempt}/2): {Error}", phone, busiType, attempt + 1, ex.Message);
                await Task.Delay(300, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _lastRequestNetworkErrors[phone] = true;
                _log.LogWarning("MeetMob [{Phone}]: {BusiType} timeout ({Timeout}s)", phone, busiType, HttpTimeout);
                SetWafCooldown(phone, 15);
                return null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _lastRequestNetworkErrors[phone] = true;
                _log.LogWarning("MeetMob [{Phone}]: {BusiType} failed: {Error}", phone, busiType, ex.Message);
                return null;
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

    private static string ExtractCookiesFromResponse(HttpResponseMessage? response)
    {
        if (response == null) return string.Empty;
        var sb = new StringBuilder();
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                var parts = cookie.Split(';')[0].Trim();
                if (parts.Contains('='))
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(parts);
                }
            }
        }
        return sb.ToString();
    }

    internal static string NormalizeMeetMobAmount(string amount) =>
        amount.Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "").Replace(",", ".");

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
