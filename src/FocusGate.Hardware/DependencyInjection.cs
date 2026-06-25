using FocusGate.Hardware.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FocusGate.Hardware;

public static class DependencyInjection
{
    public static IServiceCollection AddHardware(this IServiceCollection services)
    {
        services.AddSingleton<ModemOrchestrator>();
        return services;
    }
}
