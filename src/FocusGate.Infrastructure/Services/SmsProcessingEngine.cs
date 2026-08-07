using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FocusGate.Core.DTOs;
using FocusGate.Core.Interfaces;
using FocusGate.Core.Models;
using FocusGate.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

/// <summary>
/// Dedicated and resource-optimized SMS processing engine.
/// Handles high-throughput inbound message batching, deduplication, database queuing,
/// and instant flash SIM inbox clearing independently of USSD operations.
/// </summary>
public class SmsProcessingEngine : ISmsProcessingEngine
{
    private readonly DatabaseWriteChannel _db;
    private readonly ILogger<SmsProcessingEngine> _log;

    public SmsProcessingEngine(
        DatabaseWriteChannel db,
        ILogger<SmsProcessingEngine> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<int> ProcessIncomingSmsBatchAsync(
        IAtCommandService at,
        int modemId,
        long simCardId,
        Func<string, Task>? onMobilisRechargeDetected = null,
        CancellationToken ct = default)
    {
        if (at == null || !at.IsOpen || simCardId <= 0)
            return 0;

        List<RawSmsMessage> messages;
        try
        {
            messages = await at.ReadAllSmsAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modem {Id}: Failed to read SMS from SIM", modemId);
            return 0;
        }

        if (messages == null || messages.Count == 0)
            return 0;

        _log.LogDebug("Modem {Id}: Processing batch of {Count} SMS messages", modemId, messages.Count);

        string? rechargeContent = null;
        var tcsList = new List<Task<bool>>();
        var smsTypes = new Dictionary<string, int>();

        foreach (var msg in messages)
        {
            var tcs = new TaskCompletionSource<bool>();
            tcsList.Add(tcs.Task);

            await _db.EnqueueAsync(new DatabaseWriteChannel.WriteOperation
            {
                Type = DatabaseWriteChannel.Op.InsertSms,
                Data = new SmsRecord
                {
                    SimCardId = simCardId,
                    SenderNumber = msg.Sender ?? string.Empty,
                    Content = msg.Content ?? string.Empty,
                    ReceivedAt = msg.ReceivedAt
                },
                Completed = tcs
            });

            var smsType = DatabaseWriteChannel.ClassifySmsType(msg.Sender ?? string.Empty, msg.Content ?? string.Empty);
            smsTypes[smsType] = smsTypes.GetValueOrDefault(smsType) + 1;

            if (rechargeContent == null && IsMobilisBalanceTrigger(msg))
            {
                rechargeContent = msg.Content;
            }
        }

        bool[] results;
        try
        {
            results = await Task.WhenAll(tcsList);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modem {Id}: Some SMS writes encountered partial errors", modemId);
            results = tcsList.Select(t => t.IsCompletedSuccessfully && t.Result).ToArray();
        }

        var savedCount = results.Count(r => r);
        var skippedCount = results.Length - savedCount;

        // Immediate flash deletion to keep SIM memory clear and prevent 125002 errors
        try
        {
            await at.DeleteAllSmsAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Modem {Id}: Flash SIM inbox deletion failed", modemId);
        }

        var typeBreakdown = string.Join(", ", smsTypes.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        _log.LogInformation("Modem {Id}: Ingested {Total} SMS ({Saved} saved, {Skipped} skipped) Types: [{Types}]",
            modemId, messages.Count, savedCount, skippedCount, typeBreakdown);

        // If recharge SMS detected, notify callback asynchronously
        if (rechargeContent != null && onMobilisRechargeDetected != null)
        {
            try
            {
                await onMobilisRechargeDetected(rechargeContent);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Modem {Id}: Mobilis recharge event callback failed", modemId);
            }
        }

        return savedCount;
    }

    public static bool IsMobilisBalanceTrigger(RawSmsMessage msg)
    {
        if (msg == null || string.IsNullOrWhiteSpace(msg.Sender) || string.IsNullOrWhiteSpace(msg.Content))
            return false;

        var sender = msg.Sender.Trim();
        if (sender != "Mobilis" && sender != "77111" && sender != "610")
            return false;

        if (msg.Content.Contains("montant de", StringComparison.OrdinalIgnoreCase)
            && msg.Content.Contains("reçu", StringComparison.OrdinalIgnoreCase))
            return true;

        if (msg.Content.Contains("rechargé", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
