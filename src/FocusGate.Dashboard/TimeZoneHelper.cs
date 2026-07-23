using FocusGate.Core.Interfaces;

namespace FocusGate.Dashboard;

public static class TimeZoneHelper
{
    private static readonly TimeZoneInfo AlgeriaTz = ResolveTz();

    private static TimeZoneInfo ResolveTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers"); }
        catch { try { return TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"); } catch { return TimeZoneInfo.Local; } }
    }

    public static DateTime ToDisplayTime(this DateTime utc, IConfigProvider? config = null)
    {
        try
        {
            var u = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return TimeZoneInfo.ConvertTimeFromUtc(u, AlgeriaTz);
        }
        catch
        {
            return utc.ToLocalTime();
        }
    }
}
