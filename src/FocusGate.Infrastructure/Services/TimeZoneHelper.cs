using FocusGate.Core.Interfaces;

namespace FocusGate.Infrastructure.Services;

public static class TimeZoneHelper
{
    public static DateTime ToDisplayTime(this DateTime utc, IConfigProvider? config = null)
    {
        return utc.ToLocalTime();
    }
}
