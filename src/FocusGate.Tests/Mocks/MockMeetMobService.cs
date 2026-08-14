namespace FocusGate.Tests.Mocks;

public class MockMeetMobService
{
    private readonly Dictionary<string, decimal> _balances = new();
    private bool _sessionExpired;
    private int _loginAttempts;

    public bool IsSessionExpired => _sessionExpired;
    public int LoginAttempts => _loginAttempts;

    public Task<string?> LoginAsync(string imsi, string phone, CancellationToken ct = default)
    {
        _loginAttempts++;
        if (_sessionExpired)
        {
            _sessionExpired = false;
            return Task.FromResult<string?>("mock-token-relogin");
        }
        return Task.FromResult<string?>("mock-token");
    }

    public Task<decimal?> GetBalanceAsync(string phone, string token, CancellationToken ct = default)
    {
        if (_sessionExpired)
            return Task.FromResult<decimal?>(null);

        if (_balances.TryGetValue(phone, out var balance))
            return Task.FromResult<decimal?>(balance);

        return Task.FromResult<decimal?>(1500.00m);
    }

    // Test helpers
    public void SetBalance(string phone, decimal balance) => _balances[phone] = balance;
    public void SimulateSessionExpiry() => _sessionExpired = true;
    public void Reset() { _balances.Clear(); _sessionExpired = false; _loginAttempts = 0; }
}
