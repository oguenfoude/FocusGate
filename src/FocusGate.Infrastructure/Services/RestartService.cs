using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FocusGate.Infrastructure.Services;

public class RestartService : BackgroundService
{
    private const string PipeName = "FocusGate_Restart";
    private static readonly TimeSpan AutoRestartInterval = TimeSpan.FromHours(8);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RestartService> _logger;

    public static volatile bool IsRestarting;

    public RestartService(IHostApplicationLifetime lifetime, ILogger<RestartService> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Restart service started, listening on pipe: {Pipe}", PipeName);

        var autoRestartTask = RunAutoRestartAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(stoppingToken);

                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(stoppingToken);

                if (command == "restart")
                {
                    _logger.LogInformation("Restart signal received from Desktop");
                    await server.FlushAsync(stoppingToken);

                    await Task.Delay(500, stoppingToken);

                    _lifetime.StopApplication();
                    return;
                }
                else if (command == "stop")
                {
                    _logger.LogInformation("Stop signal received from Desktop");
                    await server.FlushAsync(stoppingToken);

                    await Task.Delay(500, stoppingToken);

                    _lifetime.StopApplication();
                    return;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Restart pipe error");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task RunAutoRestartAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AutoRestartInterval, ct);
            _logger.LogInformation("Auto-restart triggered after {Hours} hours — drain mode, waiting {DrainSeconds}s for in-flight operations...",
                (int)AutoRestartInterval.TotalHours, (int)DrainTimeout.TotalSeconds);

            IsRestarting = true;

            await Task.Delay(DrainTimeout, ct);

            _logger.LogInformation("Drain complete — shutting down for clean restart");
            _lifetime.StopApplication();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-restart task failed unexpectedly");
        }
    }
}
