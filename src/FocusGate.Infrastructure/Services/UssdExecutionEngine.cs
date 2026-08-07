using System;
using System.Threading;
using System.Threading.Tasks;
using FocusGate.Core.Interfaces;
using FocusGate.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

/// <summary>
/// Standalone USSD execution engine.
/// Handles carrier USSD protocols, balance check codes (*222#), phone detection (*101#),
/// and response parsers independently from the main modem loop.
/// </summary>
public class UssdExecutionEngine : IUssdExecutionEngine
{
    private readonly IConfigProvider _config;
    private readonly DatabaseWriteChannel _db;
    private readonly ILogger<UssdExecutionEngine> _log;

    public UssdExecutionEngine(
        IConfigProvider config,
        DatabaseWriteChannel db,
        ILogger<UssdExecutionEngine> log)
    {
        _config = config;
        _db = db;
        _log = log;
    }

    public async Task<string> SendRawUssdAsync(IAtCommandService at, string code, int timeoutMs = 15000, CancellationToken ct = default)
    {
        if (at == null || !at.IsOpen) return string.Empty;
        try
        {
            return await at.SendUssdAsync(code, timeoutMs);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "USSD execution failed for code {Code}", code);
            return string.Empty;
        }
    }

    public async Task<decimal?> QueryBalanceAsync(IAtCommandService at, int modemId, CancellationToken ct = default)
    {
        if (at == null || !at.IsOpen) return null;
        try
        {
            _log.LogDebug("Modem {Id}: Executing balance query via USSD engine...", modemId);
            return await at.GetBalanceAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modem {Id}: USSD balance query failed", modemId);
            return null;
        }
    }

    public async Task<string?> QueryPhoneNumberAsync(IAtCommandService at, int modemId, CancellationToken ct = default)
    {
        if (at == null || !at.IsOpen) return null;
        try
        {
            var phone = await at.GetPhoneNumberViaUssdAsync();
            if (string.IsNullOrEmpty(phone))
            {
                phone = await at.GetPhoneNumberViaCnumAsync();
            }
            return phone;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modem {Id}: Phone number query failed", modemId);
            return null;
        }
    }
}
