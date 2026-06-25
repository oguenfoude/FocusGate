using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FocusGate.Hardware.Services;

public class RestartService : BackgroundService
{
    private const string PipeName = "FocusGate_Restart";
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RestartService> _logger;

    public RestartService(IHostApplicationLifetime lifetime, ILogger<RestartService> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Restart service started, listening on pipe: {Pipe}", PipeName);

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
}
