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

System.Diagnostics.Process? dashboardProcess = null;

Console.CancelKeyPress += (_, e) =>
{
    Console.WriteLine("[*] Shutting down gracefully...");
    e.Cancel = true;
};

using var appCts = new CancellationTokenSource();
CancellationTokenSource? linkedCts = null;

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

DatabaseWriteChannel? writeChannel = null;

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
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("MongoDB", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console()
        .WriteTo.File(Path.Combine(PathService.LogsDirectory, "focusgate-hilink-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30))
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

try
{
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

    dashboardProcess = StartDashboard();
    if (dashboardProcess != null)
        Console.WriteLine("  Dashboard: http://localhost:5080");

    logger.LogInformation("FocusGate HiLink started | DB: {DbPath} | Machine: {Machine}",
        PathService.DatabasePath, context.MachineId);

    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        linkedCts?.Cancel();
    });

    lifetime.ApplicationStopped.Register(() =>
    {
        if (dashboardProcess != null && !dashboardProcess.HasExited)
        {
            try { dashboardProcess.Kill(); dashboardProcess.WaitForExit(3000); } catch { }
        }
    });
}
catch (Exception ex)
{
    ShowErrorDialog("FocusGate HiLink - Startup Error", $"Failed to start:\n\n{ex.Message}\n\n{ex.InnerException?.Message}");
}

await host.RunAsync();

try { writeChannel?.CompleteAsync().GetAwaiter().GetResult(); }
catch { }
linkedCts?.Cancel();
linkedCts?.Dispose();
}
finally
{
    if (dashboardProcess != null && !dashboardProcess.HasExited)
    {
        try { dashboardProcess.Kill(); dashboardProcess.WaitForExit(3000); } catch { }
        dashboardProcess.Dispose();
    }
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

static void ShowErrorDialog(string title, string message)
{
    try
    {
        MessageBoxW(IntPtr.Zero, message, title, 0x10);
    }
    catch { }
}

static System.Diagnostics.Process? StartDashboard()
{
    try
    {
        var exeDir = AppContext.BaseDirectory;
        var dashboardExe = Path.Combine(exeDir, "FocusGate.Dashboard.exe");
        if (!File.Exists(dashboardExe)) return null;

        var existing = System.Diagnostics.Process.GetProcessesByName("FocusGate.Dashboard");
        if (existing.Length > 0) return null;

        return System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dashboardExe,
            WorkingDirectory = exeDir,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
    catch { return null; }
}

[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
