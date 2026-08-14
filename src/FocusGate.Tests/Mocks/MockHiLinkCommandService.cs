using FocusGate.Core.DTOs;
using FocusGate.Core.Enums;
using FocusGate.Core.Interfaces;

namespace FocusGate.Tests.Mocks;

public class MockHiLinkCommandService : IAtCommandService
{
    private bool _isOpen;
    private string? _comPort;
    private readonly List<RawSmsMessage> _smsInbox = new();
    private readonly Dictionary<string, string> _ussdResponses = new();
    private string? _imei;
    private string? _imsi;

    public bool IsOpen => _isOpen;
    public string? ComPort => _comPort;
    public bool IsSmsInboxFull { get; set; }
    public bool LastRequestFailed { get; set; }

    public Task OpenAsync(string comPort)
    {
        _comPort = comPort;
        _isOpen = true;
        return Task.CompletedTask;
    }

    public void Close()
    {
        _isOpen = false;
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    public Task<string> SendCommandAsync(string command, int timeoutMs = 5000)
    {
        return Task.FromResult("OK");
    }

    public Task<bool> IsAliveAsync()
    {
        return Task.FromResult(_isOpen);
    }

    public Task<string> GetImeiAsync()
    {
        return Task.FromResult(_imei ?? "MOCKIMEI12345678");
    }

    public Task<string> GetImsiAsync()
    {
        return Task.FromResult(_imsi ?? "603019123456");
    }

    public Task<NetworkRegistration> GetNetworkRegistrationAsync()
    {
        return Task.FromResult(NetworkRegistration.Registered);
    }

    public Task<string> GetPhoneNumberViaUssdAsync()
    {
        return Task.FromResult("0555123456");
    }

    public Task<string> GetPhoneNumberViaCnumAsync()
    {
        return Task.FromResult("0555123456");
    }

    public Task<decimal?> GetBalanceAsync()
    {
        return Task.FromResult<decimal?>(1500.00m);
    }

    public Task<List<RawSmsMessage>> ReadAllSmsAsync()
    {
        return Task.FromResult(_smsInbox.ToList());
    }

    public Task DeleteAllSmsAsync()
    {
        _smsInbox.Clear();
        return Task.CompletedTask;
    }

    public Task<string> SendUssdAsync(string code, int timeoutMs = 15000)
    {
        if (_ussdResponses.TryGetValue(code, out var response))
            return Task.FromResult(response);

        if (code == "*222#")
            return Task.FromResult("Solde: 1500.00DA");
        if (code == "*101#")
            return Task.FromResult("0555123456");

        return Task.FromResult<string?>("")!;
    }

    public Task<bool> TrySetCharsetAsync(string charset)
    {
        return Task.FromResult(true);
    }

    public Task<bool> TryRefreshSessionAsync()
    {
        return Task.FromResult(true);
    }

    // Test helpers
    public void SetImei(string imei) => _imei = imei;
    public void SetImsi(string imsi) => _imsi = imsi;
    public void AddSms(RawSmsMessage sms) => _smsInbox.Add(sms);
    public void SetUssdResponse(string code, string response) => _ussdResponses[code] = response;
    public void ClearInbox() => _smsInbox.Clear();
}
