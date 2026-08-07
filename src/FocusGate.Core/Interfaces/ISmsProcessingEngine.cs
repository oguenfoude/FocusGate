using System;
using System.Threading;
using System.Threading.Tasks;

namespace FocusGate.Core.Interfaces;

/// <summary>
/// Dedicated SMS processing engine interface.
/// Encapsulates high-throughput batch ingestion, deduplication, SIM inbox purging, and event triggers.
/// </summary>
public interface ISmsProcessingEngine
{
    Task<int> ProcessIncomingSmsBatchAsync(
        IAtCommandService at,
        int modemId,
        long simCardId,
        Func<string, Task>? onMobilisRechargeDetected = null,
        CancellationToken ct = default);
}
