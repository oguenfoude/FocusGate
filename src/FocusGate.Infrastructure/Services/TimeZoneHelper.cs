using FocusGate.Core.Interfaces;

namespace FocusGate.Infrastructure.Services;

public static class TimeZoneHelper
{
    private static TimeZoneInfo? _cachedAlgeriaTz;
    private static TimeZoneInfo ResolveAlgeriaTz()
    {
        if (_cachedAlgeriaTz != null) return _cachedAlgeriaTz;
        try { _cachedAlgeriaTz = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers"); }
        catch { try { _cachedAlgeriaTz = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"); } catch { _cachedAlgeriaTz = TimeZoneInfo.Utc; } }
        return _cachedAlgeriaTz;
    }

    public static DateTime ToDisplayTime(this DateTime utc, IConfigProvider? config = null)
    {
        if (config != null)
        {
            var rawValue = config.Get("display.timezone_offset_hours", null);
            if (!string.IsNullOrEmpty(rawValue)
                && double.TryParse(rawValue, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var offset))
            {
                return utc.AddHours(offset);
            }
        }
        try { return TimeZoneInfo.ConvertTimeFromUtc(utc, ResolveAlgeriaTz()); }
        catch { return utc; }
    }
}
