using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FocusGate.Infrastructure.Services;

/// <summary>
/// Process-wide liveness registry. Every frequent background loop calls
/// Heartbeat.Pulse on each iteration. FreezeWatchdog inspects the snapshot
/// to detect a process-wide stall and force a clean self-restart.
/// Only loops with a cadence of 60s or faster should pulse here.
/// </summary>
public static class Heartbeat
{
    private const int MaxComponents = 512;
    private static readonly ConcurrentDictionary<string, long> Beats = new();

    /// <summary>Called by background loops at least once per iteration.</summary>
    public static void Pulse(string component)
    {
        if (Beats.Count >= MaxComponents)
            return; // safety: never grow unbounded from dynamic modem ids
        Beats[component] = DateTime.UtcNow.Ticks;
    }

    public static List<(string Name, TimeSpan Age)> Stale(TimeSpan threshold)
    {
        var now = DateTime.UtcNow.Ticks;
        return Beats
            .Select(kvp => (kvp.Key, Age: TimeSpan.FromTicks(now - kvp.Value)))
            .Where(x => x.Age > threshold)
            .OrderByDescending(x => x.Age)
            .ToList();
    }

    public static int Count => Beats.Count;
}
