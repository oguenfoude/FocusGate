using FocusGate.Core.Interfaces;

namespace FocusGate.Dashboard;

public static class TimeZoneHelper
{
    private static readonly TimeZoneInfo AlgeriaTz = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");

    public static DateTime ToDisplayTime(this DateTime utc, IConfigProvider? config = null)
    {
        var offset = config?.Get<int?>("display.timezone_offset_hours");
        if (offset.HasValue)
            return utc.AddHours(offset.Value);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, AlgeriaTz);
    }
}
