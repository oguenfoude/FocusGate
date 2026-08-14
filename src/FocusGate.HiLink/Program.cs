using FocusGate.Core.Services;
using FocusGate.HiLink.Services;
using FocusGate.Infrastructure;
using FocusGate.Infrastructure.Data;
using FocusGate.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

// Prevent Windows sleep & disable QuickEdit terminal freeze
WindowsPlatformHelper.PreventSystemSleep();
WindowsPlatformHelper.DisableConsoleQuickEdit();

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var ex = e.ExceptionObject as Exception;
    Console.WriteLine($"[FATAL] Unhandled exception: {ex?.Message ?? e.ExceptionObject.ToString()}");
    Console.WriteLine($"[FATAL] Terminating: {e.IsTerminating}");
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    // Absorb background task exceptions (e.g. socket timeouts / internet drops from 3rd-party connection pools) to prevent console noise
    e.SetObserved();
};

using var appCts = new CancellationTokenSource();
CancellationTokenSource? linkedCts = null;

Console.CancelKeyPress += (_, e) =>
{
    Console.WriteLine("[*] Shutting down gracefully...");
    e.Cancel = true;
    try { appCts.Cancel(); } catch { }
};

var mutex = new System.Threading.Mutex(true, @"Global\FocusGate_HiLink", out bool createdNew);
if (!createdNew)
{
    try
    {
        await using var client = new System.IO.Pipes.NamedPipeClientStream(".", "FocusGate_Restart", System.IO.Pipes.PipeDirection.Out);
        await client.ConnectAsync(2000);
        await using var writer = new StreamWriter(client);
        await writer.WriteLineAsync("restart");
        await writer.FlushAsync();
    }
    catch { }
    await Task.Delay(2000);
    return;
}

try
{
    var dataDir = PathService.DataDirectory;
    Directory.CreateDirectory(dataDir);
    Directory.CreateDirectory(PathService.LogsDirectory);

    var configPath = PathService.ConfigPath;
    ConfigMerger.EnsureConfig(configPath);

    int restartCount = 0;

    while (true)
    {
        DatabaseWriteChannel? writeChannel = null;

        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .UseContentRoot(AppContext.BaseDirectory)
                .ConfigureAppConfiguration((ctx, cfg) =>
                {
                    if (File.Exists(configPath))
                    {
                        cfg.AddJsonFile(configPath, optional: true, reloadOnChange: false);
                    }
                })
                .UseSerilog((ctx, lc) => lc
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                    .MinimumLevel.Override("MongoDB", LogEventLevel.Warning)
                    .WriteTo.Console(
                        restrictedToMinimumLevel: LogEventLevel.Error,
                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.File(
                        Path.Combine(PathService.LogsDirectory, "focusgate-hilink-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        restrictedToMinimumLevel: LogEventLevel.Verbose))
                .ConfigureServices((ctx, services) =>
                {
                    services.AddFocusGate(ctx.Configuration, dataDir);
                    services.AddSingleton<HiLinkModemOrchestrator>();
                    services.AddHostedService(sp => sp.GetRequiredService<HiLinkModemOrchestrator>());
                })
                .Build();

            using var scope = host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<FocusGateDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            var machineInfo = scope.ServiceProvider.GetRequiredService<MachineInfoService>();

            DatabaseInitializer.Initialize(context, logger);

            var machineIdConfig = host.Services.GetRequiredService<IConfiguration>()["machine.id"] ?? "";
            context.MachineId = string.IsNullOrEmpty(machineIdConfig) ? machineInfo.MachineId : machineIdConfig;

            if (string.IsNullOrEmpty(machineIdConfig))
            {
                PersistMachineId(configPath, context.MachineId);
                logger.LogInformation("MachineId persisted to config: {Machine}", context.MachineId);
            }

            writeChannel = scope.ServiceProvider.GetRequiredService<DatabaseWriteChannel>();
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token);
            writeChannel.Start(linkedCts.Token);

            Console.WriteLine();
            Console.WriteLine("  ┌─────────────────────────────────────────┐");
            Console.WriteLine("  │       FocusGate HiLink Gateway           │");
            Console.WriteLine("  └─────────────────────────────────────────┘");
            Console.WriteLine();
            Console.WriteLine($"  Machine  : {context.MachineId}");
            Console.WriteLine($"  Database : {PathService.DatabasePath}");
            Console.WriteLine($"  Config   : {configPath}");
            Console.WriteLine();
            Console.WriteLine("  Commands: help, status, modems, exit");
            Console.WriteLine();

            logger.LogInformation("FocusGate HiLink started | DB: {DbPath} | Machine: {Machine}",
                PathService.DatabasePath, context.MachineId);

            // Reset restart counter after 30 seconds of successful execution
            _ = Task.Run(async () =>
            {
                await Task.Delay(30000);
                restartCount = 0;
            });

            await host.RunAsync(appCts.Token);

            try { writeChannel?.CompleteAsync().GetAwaiter().GetResult(); }
            catch { }
            linkedCts?.Cancel();
            linkedCts?.Dispose();

            if (appCts.Token.IsCancellationRequested)
                break;
        }
        catch (Exception ex)
        {
            try { linkedCts?.Cancel(); } catch { }
            try { linkedCts?.Dispose(); } catch { }

            if (appCts.Token.IsCancellationRequested)
                break;

            restartCount++;
            Console.WriteLine();
            Console.WriteLine($"[!] Process error: {ex.Message}");
            Console.WriteLine($"    Auto-restarting in 5 seconds... (attempt {restartCount})");
            Console.WriteLine();
            await Task.Delay(5000);
        }
    }
}
finally
{
    mutex.ReleaseMutex();
    mutex.Dispose();
}

static void PersistMachineId(string configPath, string machineId)
{
    try
    {
        var json = File.Exists(configPath) ? File.ReadAllText(configPath) : "{}";
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var dict = new Dictionary<string, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.GetString() ?? "";
        dict["machine.id"] = machineId;
        var sorted = dict.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value);
        File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(sorted, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    catch { }
}

