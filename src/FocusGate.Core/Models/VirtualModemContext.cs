using System;
using System.Threading;
using FocusGate.Core.Enums;

namespace FocusGate.Core.Models;

/// <summary>
/// Represents an isolated virtual execution container for a single physical modem.
/// Encapsulates per-device runtime state, cancellation boundaries, and hardware metrics.
/// </summary>
public class VirtualModemContext : IDisposable
{
    public int ModemId { get; set; }
    public string ComPort { get; set; } = string.Empty;
    public bool IsHiLink { get; set; }
    public string? Imei { get; set; }
    public string? Imsi { get; set; }
    public long? PhoneNumber { get; set; }
    public long SimCardId { get; set; }
    public ModemStatus Status { get; set; } = ModemStatus.Unknown;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;

    private readonly CancellationTokenSource _cts = new();
    public CancellationToken CancellationToken => _cts.Token;

    public bool IsCancelled => _cts.IsCancellationRequested;

    public void Cancel()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }

    public void Dispose()
    {
        Cancel();
        _cts.Dispose();
    }
}
