using FocusGate.Core.DTOs;
using FocusGate.Core.Enums;

namespace FocusGate.Core.Interfaces;

public interface IAtCommandService : IDisposable
{
    Task OpenAsync(string comPort);
    Task<string>           SendCommandAsync(string command, int timeoutMs = 5000);
    Task<bool>             IsAliveAsync();
    Task<string>           GetImeiAsync();
    Task<string>           GetImsiAsync();
    Task<NetworkRegistration> GetNetworkRegistrationAsync();
    Task<string>           GetPhoneNumberViaUssdAsync();
    Task<string>           GetPhoneNumberViaCnumAsync();
    Task<decimal?>         GetBalanceAsync();
    Task<List<RawSmsMessage>> ReadAllSmsAsync();
    Task                   DeleteAllSmsAsync();
    bool                   IsSmsInboxFull { get; }
    bool                   LastRequestFailed { get; }
    Task<string>           SendUssdAsync(string code, int timeoutMs = 15000);
    Task<bool>             TrySetCharsetAsync(string charset);
    Task<bool>             TryRefreshSessionAsync();
    bool                   IsOpen { get; }
    string?                ComPort { get; }
}
