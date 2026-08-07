using System.Threading;
using System.Threading.Tasks;

namespace FocusGate.Core.Interfaces;

/// <summary>
/// Standalone USSD execution engine interface decoupled from modem looper threads.
/// Handles balance queries, phone number queries, and custom USSD commands.
/// </summary>
public interface IUssdExecutionEngine
{
    Task<string> SendRawUssdAsync(IAtCommandService at, string code, int timeoutMs = 15000, CancellationToken ct = default);
    Task<decimal?> QueryBalanceAsync(IAtCommandService at, int modemId, CancellationToken ct = default);
    Task<string?> QueryPhoneNumberAsync(IAtCommandService at, int modemId, CancellationToken ct = default);
}
